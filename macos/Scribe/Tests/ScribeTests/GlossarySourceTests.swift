import Foundation
import XCTest

/// The Settings glossary (one name for each thing), held in the source as on Windows: no text a person can read in
/// the Mac app uses a name the glossary retired, such as "hotkey", "library", "provider" or "post-processing". The
/// test reads every string literal under `Sources/Scribe`, outside comments and outside the text that never reaches a
/// person (log calls, failed assertions, regular expressions and SQL), with literals joined by `+` read as one text.
/// A literal the rules cannot place is still read: the allowlist below names each internal use with its reason, so a
/// new one is a deliberate entry rather than a gap.
///
/// The retired words are Windows' list (`tests/Scribe.Core.Tests/GlossarySourceTests.cs`, `Retired`), word for word,
/// which `testTheWindowsRulesAreTheMacRulesWordForWord` holds, then the few names that are right on Windows and wrong
/// on a Mac. Words with an ordinary use as well as a retired one (entry, term, enabled, replacement, pattern,
/// template, active, binding, Set, Hold, Toggle) are left to review, as on Windows.
///
/// What it does not read, so a pass says nothing about it: the docs (README, PORTING-PLAN.md, PRIVACY.md); the
/// built-in word packs' CSV files, whose text is data; text Scribe builds at run time from data or settings; and a
/// message put together from pieces other than literals joined with `+` (a variable or a call in between).
final class GlossarySourceTests: XCTestCase {
    struct RetiredWord: Sendable {
        let name: String
        let pattern: String
        let say: String
    }

    /// Windows' rules, in Windows' order, with what to say on a Mac.
    static let windowsRules: [RetiredWord] = [
        RetiredWord(name: "hotkey", pattern: #"\b[Hh]otkeys?\b"#, say: "shortcut"),
        RetiredWord(name: "chord", pattern: #"\b[Cc]hords?\b"#, say: "shortcut, or two keys"),
        RetiredWord(
            name: "dictation only", pattern: #"\b[Dd]ictation[- ]only\b"#, say: "shortcut without AI cleanup"),
        RetiredWord(name: "toggle mode", pattern: #"\b[Tt]oggle mode\b"#, say: "press to start and stop"),
        RetiredWord(
            name: "silence auto-stop", pattern: #"\b[Ss]ilence auto-stop\b|\b[Ee]nd dictation on silence\b"#,
            say: "Stop when I stop talking"),
        RetiredWord(name: "overlay", pattern: #"\b[Oo]verlays?\b"#, say: "recording indicator"),
        RetiredWord(name: "pill", pattern: #"\b[Pp]ills?\b"#, say: "recording indicator"),
        RetiredWord(
            name: "voice activity detection", pattern: #"\b[Vv]oice activity detection\b|\bVAD\b"#,
            say: "Trim silence"),
        RetiredWord(
            name: "post-processing", pattern: #"\b[Pp]ost-?processing\b"#, say: "your dictionary and snippets"),
        RetiredWord(name: "decode", pattern: #"\b[Dd]ecod(?:e|es|ed|ing)\b"#, say: "speech recognition"),
        RetiredWord(
            name: "transcription", pattern: #"\b[Tt]ranscri(?:be|bes|bed|ber|bing|ption|ptions|pt|pts)\b"#,
            say: "speech recognition, or what Scribe heard"),
        RetiredWord(name: "recognizer", pattern: #"\b[Rr]ecogni[sz]ers?\b"#, say: "speech model"),
        RetiredWord(
            name: "real-time factor", pattern: #"\bRTF\b|\b[Rr]eal-time factor\b"#, say: "times faster than real time"
        ),
        RetiredWord(name: "latency", pattern: #"\b[Ll]atency\b|\b[Rr]ound trip\b"#, say: "time, or how long it took"),
        RetiredWord(
            name: "percentile", pattern: #"\bP50\b|\bP95\b|\b[Pp]ercentiles?\b"#,
            say: "typical, or 19 in 20 finish within"),
        RetiredWord(name: "pipeline", pattern: #"\b[Pp]ipelines?\b"#, say: "each step"),
        RetiredWord(name: "polish", pattern: #"\b[Pp]olish(?:es|ed|ing)?\b"#, say: "AI cleanup"),
        RetiredWord(name: "Intelligence", pattern: #"\bIntelligence\b"#, say: "AI cleanup"),
        RetiredWord(name: "provider", pattern: #"\b[Pp]roviders?\b"#, say: "where AI cleanup runs, or AI service"),
        RetiredWord(name: "on-device", pattern: #"\b[Oo]n-device\b"#, say: "on this Mac"),
        RetiredWord(
            name: "endpoint",
            pattern:
                #"\bAI endpoints?\b|\b[Cc]ustom endpoints?\b|OpenAI-compatible endpoints?|\b[Ee]ndpoint URLs?\b|\b[Bb]ase URLs?\b"#,
            say: "another AI service, or server address"),
        RetiredWord(name: "az login", pattern: #"\baz login\b"#, say: "Azure CLI sign-in"),
        RetiredWord(name: "CLI", pattern: #"\bCLI\b"#, say: "command-line tool, after the product name"),
        RetiredWord(name: "licence", pattern: #"\b[Ll]icen[cs]es?\b"#, say: "subscription"),
        RetiredWord(
            name: "execution provider", pattern: #"\b[Ee]xecution providers?\b|\b[Hh]ardware runtimes?\b"#,
            say: "AI runtime for this Mac"),
        RetiredWord(name: "DPAPI", pattern: #"\bDPAPI\b"#, say: "saved encrypted in your Keychain"),
        RetiredWord(
            name: "prompt",
            pattern: #"\b(?:[Ff]rontier|[Ll]ocal|[Ss]ystem|[Cc]leanup|[Ss]tyle) prompts?\b|\b[Pp]rompt styles?\b"#,
            say: "detailed instructions, or short instructions"),
        RetiredWord(name: "raw text", pattern: #"\b[Rr]aw (?:text|transcripts?)\b"#, say: "what Scribe heard"),
        RetiredWord(name: "glossary", pattern: #"\b[Gg]lossar(?:y|ies)\b"#, say: "vocabulary"),
        RetiredWord(name: "library", pattern: #"\b[Ll]ibrar(?:y|ies)\b"#, say: "word pack"),
        RetiredWord(
            name: "trigger phrase", pattern: #"\b[Tt]rigger phrases?\b|\b[Ee]xpands to\b"#,
            say: "When you say, and Scribe types"),
        RetiredWord(name: "process name", pattern: #"\b[Pp]rocess names?\b"#, say: "apps"),
        RetiredWord(name: "playground", pattern: #"\b[Pp]layground\b|\b[Tt]est box\b"#, say: "Try dictation"),
        RetiredWord(
            name: "text insertion", pattern: #"\b[Tt]ext insertion\b|\b[Ii]njection\b"#, say: "typing into apps"),
        RetiredWord(
            name: "insertion choice", pattern: #"\bType it in\b|\bPaste it in\b"#,
            say: "Type the text, or Paste the text"),
        RetiredWord(
            name: "terminal", pattern: #"\bterminals?\b|\bTerminals\b|\b[Ss]hells?\b"#,
            say: "command windows, such as Terminal"),
        RetiredWord(name: "insight", pattern: #"\b[Ii]nsights?\b"#, say: "AI summary"),
        RetiredWord(name: "log bundle", pattern: #"\b[Ll]og bundles?\b"#, say: "diagnostics"),
        RetiredWord(name: "Settings saved", pattern: #"\bSettings saved\b"#, say: "Changes saved"),
        RetiredWord(name: "Forever", pattern: #"\bForever\b"#, say: "Until I delete them"),
        RetiredWord(name: "No unsaved changes", pattern: #"\bNo unsaved changes\b"#, say: "All changes saved"),
    ]

    /// Names that are right on Windows and wrong on a Mac.
    static let macRules: [RetiredWord] = [
        RetiredWord(name: "PC", pattern: #"\bPCs?\b"#, say: "Mac"),
        RetiredWord(name: "tray", pattern: #"\b[Tt]ray\b"#, say: "menu bar"),
        RetiredWord(name: "File Explorer", pattern: #"\bFile Explorer\b"#, say: "Finder"),
    ]

    static let retired = windowsRules + macRules

    /// Other companies' product names, which keep their own words, as on Windows; and the commands a person types,
    /// quoted as code, which keep their own spelling.
    static let ownNames: [String] = [
        #"\bAzure CLI\b"#,
        #"\bMIT License\b"#,
        #"\bMAI-Transcribe(?:-2|-1\.5)?\b"#,
        #"[`'\x{2018}]az login[`'\x{2019}]"#,
        #"[`'\x{2018}]foundry transcribe[`'\x{2019}]"#,
    ]

    /// Files whose text is never Scribe's words to a person, with why. Paths are relative to `Sources/Scribe`.
    static let excludedFiles: [String: String] = [
        "ScribeLog.swift": "The log writer and its categories.",
        "CommandLineTools.swift":
            "The command-line verbs (Scribe --transcribe-wav and the rest), for development and CI, which print to "
            + "the terminal of whoever runs them. Windows keeps such tools under tools/, which its test does not read.",
        "CommandLineInspection.swift": "The work and the output of the command-line verbs that read a user's data.",
    ]

    /// Declarations whose text is a request to the AI model, with why: a type to its closing brace, a property to
    /// the text its initializer begins with.
    static let excludedDeclarations: [(file: String, name: String, why: String)] = [
        (
            "CleanupProvider.swift", "CleanupPrompt",
            "The instructions and vocabulary format the AI model is given; Windows excludes CleanupPrompt.cs for the "
                + "same reason. Settings shows the defaults for editing exactly as the model reads them."
        ),
        ("AIDictionarySuggestions.swift", "systemPrompt", "The request the AI model is given for AI suggestions."),
        ("UsageInsight.swift", "systemPrompt", "The request the AI model is given for the AI summary."),
    ]

    /// Files whose arrays of strings are search keywords, with why. Their other text is read as usual.
    static let keywordFiles: [String: String] = [
        "SettingsSearchIndex.swift":
            "Find a setting's keywords keep the old names on purpose, so \"hotkey\" still finds the shortcut, as on "
            + "Windows. Its labels, contexts and requirements are read like any other text."
    ]

    /// Literals that hold a retired word on purpose and that no rule places, with why. Compared with white space
    /// collapsed.
    static let allowed: [(file: String, text: String, why: String)] = [
        ("AzureCredential.swift", "az login", "Words of the Azure CLI's own error, matched to recognize it."),
        ("BuiltInDictionaryLibraries.swift", "Libraries", "The bundled resource folder of the built-in word packs."),
        ("DictionaryLibraryService.swift", "Libraries", "The Application Support folder that holds custom word packs."),
        (
            "TextInjector.swift",
            "Accessibility permission is not granted. Scribe can capture audio, but text injection is unavailable "
                + "until System Settings > Privacy & Security > Accessibility allows it.",
            "Log text: the injector hands it only to its log sink (ScribeLog.legacyUnshapedLine)."
        ),
        (
            "TextInjector.swift", "Text injection finished: .",
            "Log text: the injector hands it only to its log sink (ScribeLog.legacyUnshapedLine)."
        ),
    ]

    /// The calls whose text never reaches a person. Matched on code with literals and comments blanked out, each
    /// ending at the call's opening parenthesis.
    static let hiddenCallPattern =
        #"(?<![\w.])ScribeLog\s*\.\s*\w+\s*\("#
        + #"|(?<![\w.])(?:logger|log)\s*\.\s*(?:log|trace|debug|info|notice|warning|error|critical|fault)\s*\("#
        + #"|(?<![\w.])(?:Logger|os_log|NSLog|print|debugPrint)\s*\("#
        + #"|(?<![\w.])(?:fatalError|precondition|preconditionFailure|assert|assertionFailure)\s*\("#
        + #"|(?<![\w.])(?:NSRegularExpression|Regex)\s*\("#

    /// A statement of SQL, which the database reads.
    static let sqlPattern = #"^\s*(?:SELECT|INSERT|UPDATE|DELETE|CREATE|ALTER|DROP|PRAGMA|WITH|BEGIN)\b"#

    func testNoTextAPersonReadsUsesAWordTheGlossaryRetired() throws {
        let rules = try Rules()
        var offences: [String] = []
        for item in try Self.userFacingText(rules) where !Self.isAllowed(item.file, item.text) {
            for word in rules.retired(in: item.text) {
                let place = "\(item.file):\(item.line)"
                offences.append("\(place): \"\(word.name)\" (say \(word.say)): \(Self.flat(item.text))")
            }
        }
        XCTAssertTrue(
            offences.isEmpty,
            "Text a person reads uses a word the glossary retired. Reword it, or, if no person reads it, add a rule or "
                + "an allowlist entry with the reason:\n" + offences.joined(separator: "\n"))
    }

    func testEveryExceptionToTheGlossaryIsStillNeeded() throws {
        let rules = try Rules()
        let holding = Set(
            try Self.userFacingText(rules).filter { !rules.retired(in: $0.text).isEmpty }.map {
                "\($0.file)|\(Self.flat($0.text))"
            })
        for entry in Self.allowed {
            XCTAssertFalse(entry.why.isEmpty)
            XCTAssertTrue(
                holding.contains("\(entry.file)|\(Self.flat(entry.text))"),
                "No longer needed, remove it from allowed: \(entry.file): \(entry.text)")
        }

        for (file, why) in Self.excludedFiles.merging(Self.keywordFiles, uniquingKeysWith: { first, _ in first }) {
            XCTAssertFalse(why.isEmpty)
            XCTAssertTrue(
                FileManager.default.fileExists(atPath: Self.sourceRoot.appendingPathComponent(file).path),
                "No longer exists, remove it from the exclusions: \(file)")
        }

        for entry in Self.excludedDeclarations {
            XCTAssertFalse(entry.why.isEmpty)
            let source = try String(contentsOf: Self.sourceRoot.appendingPathComponent(entry.file), encoding: .utf8)
            XCTAssertFalse(
                SwiftSourceLiterals.declarationSpans(in: source, named: entry.name).isEmpty,
                "No longer declared, remove it from excludedDeclarations: \(entry.file): \(entry.name)")
        }
    }

    /// The Windows list is the glossary: a word Windows retires or brings back is retired or brought back here too.
    func testTheWindowsRulesAreTheMacRulesWordForWord() throws {
        let path = Self.sourceRoot
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("tests", isDirectory: true)
            .appendingPathComponent("Scribe.Core.Tests", isDirectory: true)
            .appendingPathComponent("GlossarySourceTests.cs", isDirectory: false)
        let source = try String(contentsOf: path, encoding: .utf8)
        let rule = try NSRegularExpression(pattern: #"new\("([^"]+)", @"([^"]*)", "[^"]*"\)"#)
        var windows: [(name: String, pattern: String)] = []
        for match in rule.matches(in: source, range: NSRange(source.startIndex..., in: source)) {
            guard let name = Range(match.range(at: 1), in: source), let pattern = Range(match.range(at: 2), in: source)
            else {
                continue
            }
            windows.append((String(source[name]), String(source[pattern])))
        }

        XCTAssertGreaterThan(windows.count, 30)
        XCTAssertEqual(windows.map(\.name), Self.windowsRules.map(\.name), "Windows changed its retired words")
        XCTAssertEqual(windows.map(\.pattern), Self.windowsRules.map(\.pattern), "Windows changed a rule's pattern")
    }

    func testTheReaderFindsTheTextAPersonCanSeeAndNothingElse() throws {
        let source = ###"""
            // Your hotkey in a comment
            /* Your hotkey in a block comment /* nested */ still a comment */
            /// Your hotkey in a doc comment.
            struct Sample {
                func run() {
                    ScribeLog.info(.settings, "Your hotkey in a log call", .count("hotkeys", 1))
                    logger.warning("Your hotkey in a logger call")
                    assert(true, "Your hotkey in an assertion")
                }
                let pattern = try? NSRegularExpression(pattern: "Your hotkey in a pattern")
                let a = "\(hotkeyName) is only in an interpolation"
                let b = "Change your hotkey \(count) times"
                let c = #"Your "hotkey" in a raw string"#
                let d = "Your \"hotkey\" after an escaped quote"
                let e = """
                    Your hotkey in a multi-line string, "quoted" inside, \
                    and continued
                    """
                let f = "hotkeys"
                let g = "Status: \(ready ? "Your hotkey is ready" : "Not ready")"
                let h = "Change your hot"
                    + "key, it's split"
                let i = "Press \(key) as your shortcut. " + "hotkeys like it " + key + " stay apart"
                let j = "SELECT hotkey FROM settings"
                let k = ##"Your hotkey in a raw string with \##(name) and "# inside"##
            }
            """###
        let hiddenCall = try NSRegularExpression(pattern: Self.hiddenCallPattern)
        let found = SwiftSourceLiterals.read(source, hiddenCall: hiddenCall)
            .map(\.text)
            .filter { !Self.isKeyOrSQL($0) && $0.contains("hotkey") }

        XCTAssertEqual(
            found.map(Self.flat),
            [
                "Change your hotkey times",
                "Your \"hotkey\" in a raw string",
                "Your \"hotkey\" after an escaped quote",
                "Your hotkey in a multi-line string, \"quoted\" inside, and continued",
                "Change your hotkey, it's split",
                "Press as your shortcut. hotkeys like it",
                "Your hotkey in a raw string with and \"# inside",
                "Your hotkey is ready",
            ])
    }

    func testTheReaderSkipsWhatAnExclusionNames() throws {
        let source = """
            enum Prompt {
                static let text = "Your hotkey in a request to the model"
            }
            struct Shown {
                static let systemPrompt =
                    "Your hotkey in a request "
                    + "to the model"
                let label = "Your hotkey on screen"
                let keywords = entry("Your shortcut", ["hotkey", "global hotkey"], "On this Mac")
            }
            """
        let hiddenCall = try NSRegularExpression(pattern: Self.hiddenCallPattern)
        let excluded =
            SwiftSourceLiterals.declarationSpans(in: source, named: "Prompt")
            + SwiftSourceLiterals.declarationSpans(in: source, named: "systemPrompt")
            + SwiftSourceLiterals.stringArraySpans(in: source)
        let found = SwiftSourceLiterals.read(source, hiddenCall: hiddenCall, excluded: excluded).map(\.text)

        XCTAssertEqual(found, ["Your hotkey on screen", "Your shortcut", "On this Mac"])
    }

    func testTheRetiredWordsLeaveTheirReplacementsAndOwnNamesAlone() throws {
        let rules = try Rules()
        for text in [
            "Choose Change next to your shortcut.", "Your Azure account (Azure CLI)",
            "Scribe is open source under the MIT License.", "Word packs", "Where AI cleanup runs",
            "Another AI service", "Server address", "Speech recognition", "Stop when I stop talking",
            "Show the recording indicator", "Changes saved.", "On this Mac",
            "Run 'az login' in Terminal, then try again.", "Run `foundry transcribe` in Terminal to see why.",
            "Open the menu bar icon and choose Recent Dictations.",
        ] {
            XCTAssertTrue(rules.retired(in: text).isEmpty, text)
        }

        for (text, word) in [
            ("Consider a two-key chord instead.", "chord"), ("Your AI provider receives", "provider"),
            ("Couldn't reach the AI endpoint.", "endpoint"), ("The GitHub Copilot CLI is not installed.", "CLI"),
            ("This dictation used raw text.", "raw text"), ("Terminals and shells", "terminal"),
            ("Settings saved.", "Settings saved"), ("The on-device model", "on-device"), ("On this PC", "PC"),
            ("Uses your az login session.", "az login"), ("Find it in the tray.", "tray"),
        ] {
            XCTAssertTrue(rules.retired(in: text).contains { $0.name == word }, "\(word): \(text)")
        }
    }

    // MARK: - Reading the sources

    struct Item {
        let file: String
        let line: Int
        let text: String
    }

    /// The compiled rules, made once per test.
    struct Rules {
        let words: [(word: RetiredWord, regex: NSRegularExpression)]
        let ownNames: [NSRegularExpression]
        let hiddenCall: NSRegularExpression

        init() throws {
            words = try GlossarySourceTests.retired.map { ($0, try NSRegularExpression(pattern: $0.pattern)) }
            ownNames = try GlossarySourceTests.ownNames.map { try NSRegularExpression(pattern: $0) }
            hiddenCall = try NSRegularExpression(pattern: GlossarySourceTests.hiddenCallPattern)
        }

        func retired(in text: String) -> [RetiredWord] {
            var cleaned = text
            for name in ownNames {
                cleaned = name.stringByReplacingMatches(
                    in: cleaned, range: NSRange(cleaned.startIndex..., in: cleaned), withTemplate: " ")
            }
            let range = NSRange(cleaned.startIndex..., in: cleaned)
            return words.filter { $0.regex.firstMatch(in: cleaned, range: range) != nil }.map(\.word)
        }
    }

    static var sourceRoot: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("Sources", isDirectory: true)
            .appendingPathComponent("Scribe", isDirectory: true)
    }

    static func userFacingText(_ rules: Rules) throws -> [Item] {
        let root = sourceRoot.standardizedFileURL
        let walker = try XCTUnwrap(FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil))
        var items: [Item] = []
        for case let url as URL in walker where url.pathExtension == "swift" {
            let relative = String(url.standardizedFileURL.path.dropFirst(root.path.count + 1))
            guard excludedFiles[relative] == nil else {
                continue
            }
            let source = try String(contentsOf: url, encoding: .utf8)
            var excluded = excludedDeclarations.filter { $0.file == relative }.flatMap {
                SwiftSourceLiterals.declarationSpans(in: source, named: $0.name)
            }
            if keywordFiles[relative] != nil {
                excluded += SwiftSourceLiterals.stringArraySpans(in: source)
            }
            for literal in SwiftSourceLiterals.read(source, hiddenCall: rules.hiddenCall, excluded: excluded)
            where !isKeyOrSQL(literal.text) {
                items.append(Item(file: relative, line: literal.line, text: literal.text))
            }
        }
        return items.sorted { ($0.file, $0.line) < ($1.file, $1.line) }
    }

    /// A single token with no letter in upper case, or with the punctuation of a key, a path, a code or a markup tag,
    /// is a key ("hotkeys", "ai.guardrails.local", "lock.shield", "<transcript>"); SQL is read by the database.
    static func isKeyOrSQL(_ text: String) -> Bool {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty || trimmed.range(of: sqlPattern, options: .regularExpression) != nil {
            return true
        }
        guard !trimmed.contains(where: \.isWhitespace) else {
            return false
        }
        return !trimmed.contains(where: \.isUppercase) || trimmed.contains { ".-_<>/\\$:@=#".contains($0) }
    }

    static func isAllowed(_ file: String, _ text: String) -> Bool {
        allowed.contains { $0.file == file && flat($0.text) == flat(text) }
    }

    static func flat(_ text: String) -> String {
        text.split(whereSeparator: \.isWhitespace).joined(separator: " ")
    }
}
