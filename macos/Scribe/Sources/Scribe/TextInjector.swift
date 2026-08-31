import AppKit
import ApplicationServices
import Foundation
import OSLog

enum InjectionResult {
    case success
    case accessibilityDenied
    case noFocusedElement
    case fallbackUsed
}

final class TextInjector {
    private let logger = Logger(subsystem: "com.scribe.macos", category: "TextInjection")
    private let logSink: (String) -> Void

    init(logSink: @escaping (String) -> Void) {
        self.logSink = logSink
    }

    func promptForAccessibilityAccessIfNeeded() -> Bool {
        if ProcessInfo.processInfo.environment["SCRIBE_FORCE_ACCESSIBILITY_DENIED"] == "1" {
            let message = "Accessibility permission is not granted. Scribe can capture audio, but text injection is unavailable until System Settings > Privacy & Security > Accessibility allows it."
            logger.warning("\(message, privacy: .public)")
            logSink(message)
            return false
        }

        let options = ["AXTrustedCheckOptionPrompt": true] as CFDictionary
        let trusted = AXIsProcessTrustedWithOptions(options)
        if !trusted {
            let message = "Accessibility permission is not granted. Scribe can capture audio, but text injection is unavailable until System Settings > Privacy & Security > Accessibility allows it."
            logger.warning("\(message, privacy: .public)")
            logSink(message)
        }

        return trusted
    }

    func inject(text: String) -> InjectionResult {
        if ProcessInfo.processInfo.environment["SCRIBE_FORCE_ACCESSIBILITY_DENIED"] == "1" {
            let message = "Accessibility permission is not granted, so text injection is unavailable."
            logger.warning("\(message, privacy: .public)")
            logSink(message)
            return .accessibilityDenied
        }

        guard AXIsProcessTrusted() else {
            let message = "Accessibility permission is not granted, so text injection is unavailable."
            logger.warning("\(message, privacy: .public)")
            logSink(message)
            return .accessibilityDenied
        }

        guard let focusedElement = focusedElement() else {
            let message = "Text injection could not find a focused UI element in the frontmost app."
            logger.warning("\(message, privacy: .public)")
            logSink(message)
            return .noFocusedElement
        }

        if insertViaAccessibility(text: text, into: focusedElement) {
            return .success
        }

        if pasteViaClipboard(text: text) {
            return .fallbackUsed
        }

        let message = "Text injection could not update the focused element or complete the pasteboard fallback."
        logger.warning("\(message, privacy: .public)")
        logSink(message)
        return .noFocusedElement
    }

    private func focusedElement() -> AXUIElement? {
        let systemWide = AXUIElementCreateSystemWide()
        var value: CFTypeRef?
        let result = AXUIElementCopyAttributeValue(
            systemWide,
            kAXFocusedUIElementAttribute as CFString,
            &value)

        guard result == .success, let value else {
            return nil
        }

        return (value as! AXUIElement)
    }

    private func insertViaAccessibility(text: String, into element: AXUIElement) -> Bool {
        if isAttributeSettable(kAXSelectedTextAttribute as CFString, on: element) {
            let result = AXUIElementSetAttributeValue(
                element,
                kAXSelectedTextAttribute as CFString,
                text as CFTypeRef)

            if result == .success {
                logger.info("Inserted text through the selected-text Accessibility attribute.")
                return true
            }
        }

        guard
            isAttributeSettable(kAXValueAttribute as CFString, on: element),
            let currentValue = copyStringAttribute(kAXValueAttribute as CFString, from: element),
            let selectedRange = copySelectedRange(from: element)
        else {
            return false
        }

        let nsCurrentValue = currentValue as NSString
        let safeLocation = max(0, min(selectedRange.location, nsCurrentValue.length))
        let safeLength = max(0, min(selectedRange.length, nsCurrentValue.length - safeLocation))
        let replacementRange = NSRange(location: safeLocation, length: safeLength)
        let updatedValue = nsCurrentValue.replacingCharacters(in: replacementRange, with: text)

        let setValueResult = AXUIElementSetAttributeValue(
            element,
            kAXValueAttribute as CFString,
            updatedValue as CFTypeRef)

        guard setValueResult == .success else {
            return false
        }

        let newInsertionLocation = safeLocation + (text as NSString).length
        _ = setSelectedRange(
            NSRange(location: newInsertionLocation, length: 0),
            on: element)

        logger.info("Inserted text through the value Accessibility attribute.")
        return true
    }

    private func pasteViaClipboard(text: String) -> Bool {
        guard let frontmostApplication = NSWorkspace.shared.frontmostApplication else {
            return false
        }

        let pasteboard = NSPasteboard.general
        let snapshot = PasteboardSnapshot.capture(from: pasteboard)

        pasteboard.clearContents()
        guard pasteboard.setString(text, forType: .string) else {
            snapshot.restore(to: pasteboard)
            return false
        }

        Thread.sleep(forTimeInterval: 0.05)

        guard postCommandV(to: frontmostApplication.processIdentifier) else {
            snapshot.restore(to: pasteboard)
            return false
        }

        Thread.sleep(forTimeInterval: 0.15)
        snapshot.restore(to: pasteboard)
        logger.info("Inserted text through the pasteboard fallback path.")
        return true
    }

    private func postCommandV(to pid: pid_t) -> Bool {
        guard let source = CGEventSource(stateID: .hidSystemState) else {
            return false
        }

        let commandKeyCode: CGKeyCode = 55
        let vKeyCode: CGKeyCode = 9

        let events = [
            CGEvent(keyboardEventSource: source, virtualKey: commandKeyCode, keyDown: true),
            CGEvent(keyboardEventSource: source, virtualKey: vKeyCode, keyDown: true),
            CGEvent(keyboardEventSource: source, virtualKey: vKeyCode, keyDown: false),
            CGEvent(keyboardEventSource: source, virtualKey: commandKeyCode, keyDown: false)
        ]

        guard events.allSatisfy({ $0 != nil }) else {
            return false
        }

        events[0]?.postToPid(pid)
        events[1]?.flags = .maskCommand
        events[1]?.postToPid(pid)
        events[2]?.flags = .maskCommand
        events[2]?.postToPid(pid)
        events[3]?.postToPid(pid)
        return true
    }

    private func copyStringAttribute(_ attribute: CFString, from element: AXUIElement) -> String? {
        var value: CFTypeRef?
        let result = AXUIElementCopyAttributeValue(element, attribute, &value)
        guard result == .success, let string = value as? String else {
            return nil
        }

        return string
    }

    private func copySelectedRange(from element: AXUIElement) -> NSRange? {
        var value: CFTypeRef?
        let result = AXUIElementCopyAttributeValue(
            element,
            kAXSelectedTextRangeAttribute as CFString,
            &value)

        guard result == .success, let value else {
            return nil
        }

        let axValue = value as! AXValue

        guard AXValueGetType(axValue) == .cfRange else {
            return nil
        }

        var range = CFRange()
        guard AXValueGetValue(axValue, .cfRange, &range) else {
            return nil
        }

        return NSRange(location: range.location, length: range.length)
    }

    private func setSelectedRange(_ range: NSRange, on element: AXUIElement) -> Bool {
        guard isAttributeSettable(kAXSelectedTextRangeAttribute as CFString, on: element) else {
            return false
        }

        var cfRange = CFRange(location: range.location, length: range.length)
        guard let axValue = AXValueCreate(.cfRange, &cfRange) else {
            return false
        }

        let result = AXUIElementSetAttributeValue(
            element,
            kAXSelectedTextRangeAttribute as CFString,
            axValue)

        return result == .success
    }

    private func isAttributeSettable(_ attribute: CFString, on element: AXUIElement) -> Bool {
        var isSettable: DarwinBoolean = false
        let result = AXUIElementIsAttributeSettable(element, attribute, &isSettable)
        return result == .success && isSettable.boolValue
    }
}

private struct PasteboardSnapshot {
    let items: [NSPasteboardItem]

    static func capture(from pasteboard: NSPasteboard) -> PasteboardSnapshot {
        let items = (pasteboard.pasteboardItems ?? []).map { item in
            let copy = NSPasteboardItem()
            for type in item.types {
                if let data = item.data(forType: type) {
                    copy.setData(data, forType: type)
                }
            }
            return copy
        }

        return PasteboardSnapshot(items: items)
    }

    func restore(to pasteboard: NSPasteboard) {
        pasteboard.clearContents()
        if !items.isEmpty {
            pasteboard.writeObjects(items)
        }
    }
}
