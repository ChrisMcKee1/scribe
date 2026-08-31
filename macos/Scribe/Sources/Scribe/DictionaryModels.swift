import Foundation

/// A single user-dictionary substitution applied during post-processing. When `wholeWord` is set
/// the pattern matches on word boundaries; otherwise it's a plain case-insensitive phrase
/// replacement. Mirrors `Scribe.Core.Models.DictionaryEntry` on Windows.
struct DictionaryEntry: Equatable {
    let id: Int64
    var pattern: String
    var replacement: String
    var wholeWord: Bool
    var enabled: Bool

    init(id: Int64 = 0, pattern: String, replacement: String, wholeWord: Bool = true, enabled: Bool = true) {
        self.id = id
        self.pattern = pattern
        self.replacement = replacement
        self.wholeWord = wholeWord
        self.enabled = enabled
    }
}

/// A voice snippet: speaking the trigger `phrase` expands to the (possibly multi-line) `template`
/// during post-processing. Distinct from a dictionary entry: templates can be long, are matched as
/// a whole phrase, and are never folded into the AI cleanup glossary. Mirrors
/// `Scribe.Core.Models.Snippet` on Windows.
struct Snippet: Equatable {
    let id: Int64
    var phrase: String
    var template: String
    var enabled: Bool

    init(id: Int64 = 0, phrase: String, template: String, enabled: Bool = true) {
        self.id = id
        self.phrase = phrase
        self.template = template
        self.enabled = enabled
    }
}
