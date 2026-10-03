import Foundation

enum SettingsDictionaryPageLogic {
    static func matchesSearch(_ entry: DictionaryEntry, query: String) -> Bool {
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return true }
        return entry.pattern.localizedCaseInsensitiveContains(trimmed)
            || entry.replacement.localizedCaseInsensitiveContains(trimmed)
    }

    static func enabledSummary(enabled: Int, total: Int, noun: String) -> String {
        let plural = total == 1 ? noun : "\(noun)s"
        let verb = total == 1 ? "is" : "are"
        return "\(enabled.formatted()) of \(total.formatted()) \(plural) \(verb) on."
    }

    static func wordPackRows(_ libraries: [DictionaryLibrary]) -> [DictionaryLibrary] {
        libraries.sorted { left, right in
            let name = left.name.localizedCaseInsensitiveCompare(right.name)
            if name != .orderedSame { return name == .orderedAscending }
            return left.id.localizedCaseInsensitiveCompare(right.id) == .orderedAscending
        }
    }

    static func enabledSpokenForms(_ libraries: [DictionaryLibrary], enabledIds: Set<String>) -> Set<String> {
        Set(
            libraries
                .filter { enabledIds.contains($0.id) }
                .flatMap(\.enabledEntries)
                .map { normalizedSpokenForm($0.pattern) }
                .filter { !$0.isEmpty }
        )
    }

    static func normalizedSpokenForm(_ value: String) -> String {
        value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
    }
}
