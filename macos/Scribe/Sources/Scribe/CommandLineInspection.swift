import Foundation

/// The work behind the command-line verbs that look at a user's data: `--post-process-text`, `--resolve-profile`
/// and `--diagnostics`. Each reads the user's database without changing it (`PersistenceAccess.readOnly`, which
/// also never creates a missing one). When the user has no rules or profiles to try, the first two run fixed
/// verification fixtures instead, in a temporary database of their own that is deleted afterwards, never the user's.
enum CommandLineInspection {
    /// What `--post-process-text` produced.
    struct PostProcessOutcome: Equatable, Sendable {
        let text: String
        /// True when the user had no rules or snippets, so the fixtures were used.
        let usedFixtures: Bool
    }

    /// The profile `--resolve-profile` matched, and the text it produced.
    struct ProfileOutcome: Equatable {
        let profile: AppProfile?
        let newlineMode: NewlineInjectionMode
        let text: String
        /// True when the user had no profiles, so the fixtures were used.
        let usedFixtures: Bool
    }

    private static let fixtureDictionary = [
        DictionaryEntry(pattern: "sherpa onnx", replacement: "sherpa-onnx"),
        DictionaryEntry(pattern: "github", replacement: "GitHub"),
    ]

    private static let fixtureSnippets = [Snippet(phrase: "sign off block", template: "Best regards,\nScribe Team")]

    private static let fixtureProfiles = [
        AppProfile(
            name: "Terminal",
            bundleIdentifiers: ["com.apple.Terminal", "com.googlecode.iterm2"],
            processNames: ["Terminal", "iTerm2"],
            writingStylePrompt: "Be extremely terse. No filler words.",
            newlineHandling: .alwaysFlatten),
        AppProfile(
            name: "Email",
            bundleIdentifiers: ["com.apple.mail", "com.microsoft.Outlook"],
            processNames: ["Mail", "Microsoft Outlook"],
            writingStylePrompt: "Use a formal, professional tone with complete sentences.",
            newlineHandling: .keepNewlines),
    ]

    /// Runs `transcript` through the enabled dictionary rules and snippets in the database at `database`, or through
    /// the fixtures, in a temporary database under `scratch`, when it has neither.
    static func postProcess(_ transcript: String, database: URL, scratch: URL) throws -> PostProcessOutcome {
        var dictionary: [DictionaryEntry] = []
        var snippets: [Snippet] = []
        try readIfPresent(database) { store in
            dictionary = try store.fetchEnabledDictionaryEntries()
            snippets = try store.fetchEnabledSnippets()
        }
        let usedFixtures = dictionary.isEmpty && snippets.isEmpty
        if usedFixtures {
            try withFixtureStore(in: scratch) { store in
                for entry in fixtureDictionary {
                    _ = try store.insertDictionaryEntry(entry)
                }
                for snippet in fixtureSnippets {
                    _ = try store.insertSnippet(snippet)
                }
                dictionary = try store.fetchEnabledDictionaryEntries()
                snippets = try store.fetchEnabledSnippets()
            }
        }

        let processor = TextPostProcessor()
        processor.reload(dictionaryEntries: dictionary, snippets: snippets)
        return PostProcessOutcome(text: processor.process(transcript), usedFixtures: usedFixtures)
    }

    /// Matches `bundleIdentifier` against the app profiles in the database at `database`, or against the fixtures, in
    /// a temporary database under `scratch`, when it has none, and applies the matched newline mode to `text`.
    static func resolveProfile(
        bundleIdentifier: String, text: String, database: URL, scratch: URL
    ) throws -> ProfileOutcome {
        var profiles: [AppProfile] = []
        try readIfPresent(database) { store in
            profiles = try store.fetchAppProfiles()
        }
        let usedFixtures = profiles.isEmpty
        if usedFixtures {
            try withFixtureStore(in: scratch) { store in
                for profile in fixtureProfiles {
                    _ = try store.insertAppProfile(profile)
                }
                profiles = try store.fetchAppProfiles()
            }
        }

        let matched = AppProfileMatcher.match(profiles: profiles, bundleIdentifier: bundleIdentifier, processName: nil)
        let mode = AppProfileMatcher.resolveNewlineMode(
            profile: matched, globalDefault: .smartFlatten, bundleIdentifier: bundleIdentifier)
        let result = AppProfileMatcher.applyNewlineMode(mode, to: text, bundleIdentifier: bundleIdentifier)
        return ProfileOutcome(profile: matched, newlineMode: mode, text: result, usedFixtures: usedFixtures)
    }

    /// The Diagnostics figures for the window that starts at `since`, read as the Diagnostics tab reads them: the
    /// window's newest dictations, with one row past the cap to tell whether the window holds more.
    static func diagnostics(database: URL, since: Date) throws -> DiagnosticsWindowSummary {
        var records: [DictationHistoryRecord] = []
        try readIfPresent(database) { store in
            records = try store.fetchDictationHistory(since: since, limit: DiagnosticsSettingsAccess.readLimit + 1)
        }
        return DiagnosticsWindowSummary(records: records, since: since)
    }

    /// What `--diagnostics` prints for a window reaching back `days` days, the coverage note included when the
    /// window held more dictations than one read covers.
    static func diagnosticsReport(_ window: DiagnosticsWindowSummary, days: Double) -> String {
        guard let snapshot = window.stats else {
            return "No dictations in the last \(days) day(s)."
        }

        var lines = [
            "Dictations: \(snapshot.count)",
            String(
                format: "Total audio: %.1f s (longest %.1f s)", snapshot.totalAudioSeconds,
                snapshot.longestAudioSeconds),
        ]
        if let decodeMs = snapshot.decodeMs {
            lines.append(
                String(
                    format: "Decode ms: avg %.0f, p50 %.0f, p95 %.0f, min %.0f, max %.0f (n=%d)",
                    decodeMs.average, decodeMs.p50, decodeMs.p95, decodeMs.min, decodeMs.max, snapshot.decodeCount))
            lines.append(
                String(
                    format: "RTF: fastest %.3f, p50 %.3f, p95 %.3f", snapshot.fastestRtf, snapshot.rtfP50,
                    snapshot.rtfP95))
        } else {
            lines.append("Decode ms: no timed dictations yet.")
        }
        if let cleanupMs = snapshot.cleanupMs {
            lines.append(
                String(
                    format: "Cleanup ms: avg %.0f, min %.0f, max %.0f (n=%d)",
                    cleanupMs.average, cleanupMs.min, cleanupMs.max, snapshot.cleanupCount))
        }
        if window.capped {
            lines.append(DiagnosticsWindowSummary.coverageNote)
        }
        return lines.joined(separator: "\n")
    }

    /// Runs `body` over the database at `url`, opened read-only, or does nothing when there is no file there, so a
    /// Mac where Scribe never ran is not given a database by a verb.
    private static func readIfPresent(_ url: URL, _ body: (PersistenceStore) throws -> Void) throws {
        guard FileManager.default.fileExists(atPath: url.path(percentEncoded: false)) else {
            return
        }
        let store = PersistenceStore(databaseURL: url, access: .readOnly)
        defer { store.closeConnection() }
        try body(store)
    }

    /// Runs `body` over a new database in a directory of its own under `scratch`, removed afterwards.
    private static func withFixtureStore(in scratch: URL, _ body: (PersistenceStore) throws -> Void) throws {
        let directory = scratch.appendingPathComponent("scribe-verification-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = PersistenceStore(databaseURL: directory.appendingPathComponent("scribe.db", isDirectory: false))
        defer { store.closeConnection() }
        try store.initialize()
        try body(store)
    }
}
