import Foundation

/// The insertion step of one finished dictation: what is kept for recovery and history, and what is handed to the
/// target. With the typing setting on, the target gets one trailing space unless the text is empty or already ends in
/// white space, while everything that keeps text stores it as dictated.
enum DictationInsertion {
    /// What the target is given for `text`. With `addSpaceAfterDictation` on, a dictation that is not empty and does
    /// not already end in any Unicode white-space character gets one trailing space.
    static func textToType(_ text: String, addSpaceAfterDictation: Bool) -> String {
        guard addSpaceAfterDictation, let last = text.last, !last.isWhitespace else {
            return text
        }
        return text + " "
    }

    /// Keeps `text` for recovery first, then checks cancellation, and only then asks `inject` to deliver the text the
    /// target should receive.
    static func insert(
        _ text: String,
        addSpaceAfterDictation: Bool,
        recovery: LastTranscriptStore,
        inject: @escaping @MainActor @Sendable (String) async -> InjectionResult
    ) async -> DictationInsertionResult {
        recovery.set(text)
        let recoveryGeneration = recovery.generation
        let typed = textToType(text, addSpaceAfterDictation: addSpaceAfterDictation)
        guard !Task.isCancelled else {
            return DictationInsertionResult(
                recorded: text,
                typed: typed,
                recoveryGeneration: recoveryGeneration,
                injection: InjectionResult(delivery: .cancelled))
        }
        return DictationInsertionResult(
            recorded: text,
            typed: typed,
            recoveryGeneration: recoveryGeneration,
            injection: await inject(typed))
    }
}

struct DictationInsertionResult: Equatable, Sendable {
    let recorded: String
    let typed: String
    let recoveryGeneration: UInt64
    let injection: InjectionResult

    var spaceAdded: Bool {
        typed.count != recorded.count
    }
}
