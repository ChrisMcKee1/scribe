import Foundation

/// A named, categorized collection of dictionary substitutions that can be switched on as a unit
/// and layered on top of the user's base dictionary. Built-in libraries ship embedded in the app
/// bundle; custom libraries are imported from CSV files the user supplies. Entries carry no
/// database id (`id: 0`): a library is composed into the effective dictionary in memory, never
/// written into the `dictionary_entries` table, so enabling or disabling one never touches the
/// user's own entries. Direct port of Windows' `Scribe.Core.PostProcessing.DictionaryLibrary`.
struct DictionaryLibrary: Equatable {
    let id: String
    let name: String
    let category: String
    let description: String?
    let builtIn: Bool
    let entries: [DictionaryEntry]

    /// Only the entries whose `enabled` flag is set.
    var enabledEntries: [DictionaryEntry] { entries.filter(\.enabled) }

    /// Count of enabled entries, shown in the library list.
    var enabledEntryCount: Int { entries.count(where: \.enabled) }
}
