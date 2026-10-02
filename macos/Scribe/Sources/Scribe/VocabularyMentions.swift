import Foundation

/// Which vocabulary entries a dictation appears to mention, so AI cleanup can carry those rather than the whole
/// vocabulary. Matching is deliberately inclusive: a mistaken extra term costs a few prompt tokens, while a missed
/// term can cost the spelling it was there to protect.
enum VocabularyMentions {
    private static let commonWords: Set<String> = [
        "the", "and", "for", "with", "from", "into", "onto", "that", "this", "your", "you", "our", "are", "was",
        "not", "but", "all", "any", "can", "has", "have", "one", "two", "new", "use", "get", "set", "run", "out",
        "off", "via", "per", "max", "min", "its", "his", "her", "who", "why", "how", "what", "when", "where",
        "which", "will", "just", "large", "small", "model", "models", "version", "preview", "latest", "base",
        "chat", "instruct",
    ]

    static func select(_ entries: [DictionaryEntry], _ dictation: String?) -> [DictionaryEntry] {
        guard let dictation, !dictation.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, !entries.isEmpty else {
            return []
        }

        let text = DictationWords(parts(dictation))
        guard !text.isEmpty else {
            return []
        }

        var selected: [DictionaryEntry] = []
        selected.reserveCapacity(entries.count)
        for entry in entries where entry.enabled {
            if mentions(text, form: entry.replacement) || mentions(text, form: entry.pattern) {
                selected.append(entry)
            }
        }
        return selected
    }

    static func mentions(_ text: DictationWords, form: String?) -> Bool {
        guard let form, !form.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return false
        }

        let parts = parts(form)
        guard !parts.isEmpty else {
            return false
        }

        let joined = parts.joined()
        if joined.count >= 3, text.hasJoinedRun(joined) {
            return true
        }

        if parts.count >= 2 {
            let sound = soundOf(parts)
            if sound.count >= 3, text.hasSoundRun(sound) {
                return true
            }
        }

        var words = 0
        for part in parts {
            guard part.count >= 3, let first = part.first, first.isLetter, !commonWords.contains(part) else {
                continue
            }
            words += 1
            if !text.hasWord(part) {
                return false
            }
        }

        return words > 0
    }

    static func parts(_ text: String) -> [String] {
        var parts: [String] = []
        var currentScalars = String.UnicodeScalarView()
        var currentIsDigit: Bool?

        func flush() {
            guard !currentScalars.isEmpty else {
                return
            }
            parts.append(String(currentScalars).lowercased())
            currentScalars.removeAll()
            currentIsDigit = nil
        }

        for scalar in text.unicodeScalars {
            let isLetter = CharacterSet.letters.contains(scalar)
            let isDigit = CharacterSet.decimalDigits.contains(scalar)
            if !isLetter && !isDigit {
                flush()
                continue
            }

            if let currentIsDigit, currentIsDigit != isDigit {
                flush()
            }

            currentScalars.append(scalar)
            currentIsDigit = isDigit
        }

        flush()
        return parts
    }

    static func soundKey(_ word: String) -> String {
        guard let first = word.first, first.isLetter else {
            return ""
        }

        let characters = Array(word.lowercased())
        var key = String()
        key.reserveCapacity(characters.count)

        for index in characters.indices {
            let character = characters[index]
            let next = characters.indices.contains(index + 1) ? characters[index + 1] : "\0"

            let mapped: Character?
            switch character {
            case "a", "e", "i", "o", "u", "y":
                mapped = index == 0 ? "a" : nil
            case "h", "w":
                mapped = nil
            case "p" where next == "h":
                mapped = "f"
            case "c" where next == "e" || next == "i" || next == "y":
                mapped = "s"
            case "c", "k", "q", "x":
                mapped = "k"
            case "g" where next == "e" || next == "i" || next == "y":
                mapped = "j"
            case "j":
                mapped = "j"
            case "z":
                mapped = "s"
            case "v":
                mapped = "f"
            case "b":
                mapped = "b"
            case "d", "t":
                mapped = "t"
            default:
                mapped = character.isLetter ? character : nil
            }

            if let mapped, key.last != mapped {
                key.append(mapped)
            }
        }

        return key
    }

    private static func soundOf(_ parts: [String]) -> String {
        var sound = String()
        for part in parts {
            if let first = part.first, first.isNumber {
                sound += part
            } else {
                sound += soundKey(part)
            }
        }
        return sound
    }

    static func distance(_ a: String, _ b: String, max: Int) -> Int {
        let aChars = Array(a)
        let bChars = Array(b)
        if abs(aChars.count - bChars.count) > max {
            return max + 1
        }

        var previous = Array(0...bChars.count)
        var current = Array(repeating: 0, count: bChars.count + 1)

        for i in 1...aChars.count {
            current[0] = i
            var best = current[0]
            for j in 1...bChars.count {
                let cost = aChars[i - 1] == bChars[j - 1] ? 0 : 1
                current[j] = min(min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost)
                best = min(best, current[j])
            }

            if best > max {
                return max + 1
            }

            swap(&previous, &current)
        }

        return previous[bChars.count]
    }

    private static func allowance(_ length: Int) -> Int {
        if length >= 10 {
            return 2
        }
        if length >= 5 {
            return 1
        }
        return 0
    }

    struct DictationWords {
        private static let maxRun = 6

        private let words: Set<String>
        private let soundKeys: Set<String>
        private let wordsByLength: [Int: [String]]
        private let runs: Set<String>
        private let runsByShape: [RunShape: [String]]
        private let soundRuns: Set<String>

        let isEmpty: Bool

        init(_ parts: [String]) {
            isEmpty = parts.isEmpty
            var words = Set<String>()
            var soundKeys = Set<String>()
            var wordsByLength: [Int: [String]] = [:]
            var runs = Set<String>()
            var runsByShape: [RunShape: [String]] = [:]
            var soundRuns = Set<String>()

            let sounds = parts.map { part -> String in
                if let first = part.first, first.isNumber {
                    return part
                }
                return VocabularyMentions.soundKey(part)
            }

            for (index, part) in parts.enumerated() {
                guard words.insert(part).inserted, let first = part.first, first.isLetter else {
                    continue
                }

                if part.count >= 4 {
                    soundKeys.insert(sounds[index])
                }
                wordsByLength[part.count, default: []].append(part)
            }

            for start in parts.indices {
                var joined = String()
                var sound = String()
                for end in start..<min(parts.count, start + Self.maxRun) {
                    joined += parts[end]
                    sound += sounds[end]
                    if runs.insert(joined).inserted, let first = joined.first {
                        let shape = RunShape(first: first, length: joined.count)
                        runsByShape[shape, default: []].append(joined)
                    }
                    if end > start {
                        soundRuns.insert(sound)
                    }
                }
            }

            self.words = words
            self.soundKeys = soundKeys
            self.wordsByLength = wordsByLength
            self.runs = runs
            self.runsByShape = runsByShape
            self.soundRuns = soundRuns
        }

        func hasWord(_ word: String) -> Bool {
            if words.contains(word) {
                return true
            }

            if word.count >= 4, soundKeys.contains(VocabularyMentions.soundKey(word)) {
                return true
            }

            let allowance = VocabularyMentions.allowance(word.count)
            guard allowance > 0 else {
                return false
            }

            for length in (word.count - allowance)...(word.count + allowance) {
                guard let candidates = wordsByLength[length] else {
                    continue
                }
                for candidate in candidates
                where VocabularyMentions.distance(word, candidate, max: allowance) <= allowance {
                    return true
                }
            }

            return false
        }

        func hasJoinedRun(_ joined: String) -> Bool {
            if runs.contains(joined) {
                return true
            }

            let allowance = VocabularyMentions.allowance(joined.count)
            guard allowance > 0, let first = joined.first else {
                return false
            }

            for length in (joined.count - allowance)...(joined.count + allowance) {
                guard let candidates = runsByShape[RunShape(first: first, length: length)] else {
                    continue
                }
                for candidate in candidates
                where VocabularyMentions.distance(joined, candidate, max: allowance) <= allowance {
                    return true
                }
            }

            return false
        }

        func hasSoundRun(_ sound: String) -> Bool {
            soundRuns.contains(sound)
        }

        private struct RunShape: Hashable {
            let first: Character
            let length: Int
        }
    }
}
