import Foundation

enum LibraryEditor {
    static let metadataDoubleQuoteMessage =
        "Names can't contain a double quote (\"), which older versions of Scribe misread."

    static func commitSpoken(_ typed: String?) -> String {
        LibraryTermKey.normalize(typed)
    }

    static func commitWritten(_ typed: String?) -> String {
        (typed ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func commit(_ typed: TermValues) -> TermValues {
        TermValues(commitSpoken(typed.spoken), commitWritten(typed.written), typed.wholeWord, typed.enabled)
    }

    static func commitChanges(displayed: TermValues, typed: TermValues) -> TermValues {
        TermValues(
            changedText(displayed.spoken, typed.spoken, commitSpoken),
            changedText(displayed.written, typed.written, commitWritten),
            typed.wholeWord,
            typed.enabled)
    }

    static func isWellFormed(_ text: String?) -> Bool {
        guard let text, !text.isEmpty else {
            return true
        }
        let units = Array(text.utf16)
        var index = 0
        while index < units.count {
            let unit = units[index]
            if UTF16.isLeadSurrogate(unit) {
                guard index + 1 < units.count, UTF16.isTrailSurrogate(units[index + 1]) else {
                    return false
                }
                index += 2
                continue
            }
            if UTF16.isTrailSurrogate(unit) {
                return false
            }
            index += 1
        }
        return true
    }

    static func availableCommands(for row: LibraryRow, editingText: Bool) -> TermCommands {
        var commands: TermCommands = [.copy, .copyToDictionary]
        if row.values.enabled {
            commands.insert(.turnOff)
        } else {
            commands.insert(.turnOn)
            commands.insert(.showOtherSources)
        }

        switch row.origin {
        case .custom, .added, .noLongerShipped:
            commands.insert(.delete)
        case .edited, .pinned:
            commands.formUnion(row.shipped == nil ? .delete : .restoreBuiltIn)
        case .off, .shipped:
            break
        }

        if editingText {
            commands.remove(.delete)
            commands.remove(.turnOff)
            commands.remove(.turnOn)
        }
        return commands
    }

    static func message(
        for issue: LibraryValidationIssue,
        spoken: String? = nil,
        state: LibraryFileState = .available,
        otherSpoken: String? = nil
    ) -> String {
        let quoted = LibraryTermKey.normalize(spoken)
        let other = LibraryTermKey.normalize(otherSpoken)
        switch issue.kind {
        case .writtenWithoutSpoken:
            return "Type what you say before how it should be written."
        case .duplicateSpoken where !quoted.isEmpty && !other.isEmpty && !LibraryTermKey.areSame(quoted, other):
            return "\"\(quoted)\" is already in this word pack as the term you changed to \"\(other)\"."
        case .duplicateSpoken:
            if quoted.isEmpty {
                return "This term is already in this word pack."
            }
            return "\"\(quoted)\" is already in this word pack."
        case .emptyWrittenWithoutIntent:
            if quoted.isEmpty {
                return "Type how this should be written."
            }
            return "Type how \"\(quoted)\" should be written."
        case .fieldTooLong:
            return "This is longer than \(LibraryLimits.maxFieldLength.formatted()) characters. Shorten it to save."
        case .tooManyTerms:
            return "A word pack can hold up to \(LibraryLimits.maxTermsPerLibrary.formatted()) terms."
        case .emptyName:
            return "Type a name for this word pack."
        case .duplicateName:
            return "Another word pack already has this name."
        case .metadataDoubleQuote, .metadataUnreadableInOlder:
            switch issue.metadata {
            case .category:
                return "Categories can't contain a double quote (\"), which older versions of Scribe misread."
            case .description:
                return "Descriptions can't contain a double quote (\"), which older versions of Scribe misread."
            default:
                return metadataDoubleQuoteMessage
            }
        case .contentNotSaveable:
            switch state {
            case .partlyReadable:
                return "Some rows of this word pack couldn't be read, so it can't be edited here. "
                    + "Import the file again to see them."
            case .awaitingRelease:
                return "This word pack is open in another app. Close it there to make changes."
            default:
                return "This word pack couldn't be read, so it can't be edited here."
            }
        case .malformedText:
            return "This text has a broken character that can't be saved. Delete it and type it again."
        }
    }

    private static func changedText(_ displayed: String, _ typed: String, _ commit: (String?) -> String) -> String {
        displayed == typed ? displayed : commit(typed)
    }
}
