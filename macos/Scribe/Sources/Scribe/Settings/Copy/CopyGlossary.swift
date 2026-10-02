import Foundation

/// A name the Settings redesign retired, the pattern that finds it and what to say instead.
struct RetiredWord: Sendable {
    let name: String
    let pattern: String
    let use: String
}

/// The glossary (one name for each thing), held mechanically: no text a person reads in Scribe uses a retired name.
/// The first forty-one rules are the Windows list (`tests/Scribe.Core.Tests/GlossarySourceTests.cs`, `Retired`);
/// the rest are names that are wrong on a Mac. Words with an ordinary use as well as a retired one are left to review.
enum CopyGlossary {
    /// Other companies' product names, which keep their own words.
    static let productNames: [String] = [#"\bAzure CLI\b"#, #"\bMIT License\b"#]

    static let retired: [RetiredWord] = windowsRules + macRules

    /// The Windows rules, in the Windows order.
    static let windowsRules: [RetiredWord] = [
        RetiredWord(name: "hotkey", pattern: #"\b[Hh]otkeys?\b"#, use: "shortcut"),
        RetiredWord(name: "chord", pattern: #"\b[Cc]hords?\b"#, use: "shortcut, or two keys"),
        RetiredWord(
            name: "dictation only", pattern: #"\b[Dd]ictation[- ]only\b"#, use: "shortcut without AI cleanup"),
        RetiredWord(name: "toggle mode", pattern: #"\b[Tt]oggle mode\b"#, use: "press to start and stop"),
        RetiredWord(
            name: "silence auto-stop",
            pattern: #"\b[Ss]ilence auto-stop\b|\b[Ee]nd dictation on silence\b"#,
            use: "Stop when I stop talking"),
        RetiredWord(name: "overlay", pattern: #"\b[Oo]verlays?\b"#, use: "recording indicator"),
        RetiredWord(name: "pill", pattern: #"\b[Pp]ills?\b"#, use: "recording indicator"),
        RetiredWord(
            name: "voice activity detection",
            pattern: #"\b[Vv]oice activity detection\b|\bVAD\b"#,
            use: "Trim silence"),
        RetiredWord(
            name: "post-processing", pattern: #"\b[Pp]ost-?processing\b"#, use: "your dictionary and snippets"),
        RetiredWord(name: "decode", pattern: #"\b[Dd]ecod(?:e|es|ed|ing)\b"#, use: "speech recognition"),
        RetiredWord(
            name: "transcription",
            pattern: #"\b[Tt]ranscri(?:be|bes|bed|ber|bing|ption|ptions|pt|pts)\b"#,
            use: "speech recognition, or what Scribe heard"),
        RetiredWord(name: "recognizer", pattern: #"\b[Rr]ecogni[sz]ers?\b"#, use: "speech model"),
        RetiredWord(
            name: "real-time factor",
            pattern: #"\bRTF\b|\b[Rr]eal-time factor\b"#,
            use: "times faster than real time"),
        RetiredWord(
            name: "latency", pattern: #"\b[Ll]atency\b|\b[Rr]ound trip\b"#, use: "time, or how long it took"),
        RetiredWord(
            name: "percentile",
            pattern: #"\bP50\b|\bP95\b|\b[Pp]ercentiles?\b"#,
            use: "typical, or 19 in 20 finish within"),
        RetiredWord(name: "pipeline", pattern: #"\b[Pp]ipelines?\b"#, use: "each step"),
        RetiredWord(name: "polish", pattern: #"\b[Pp]olish(?:es|ed|ing)?\b"#, use: "AI cleanup"),
        RetiredWord(name: "Intelligence", pattern: #"\bIntelligence\b"#, use: "AI cleanup"),
        RetiredWord(
            name: "provider", pattern: #"\b[Pp]roviders?\b"#, use: "where AI cleanup runs, or AI service"),
        RetiredWord(name: "on-device", pattern: #"\b[Oo]n-device\b"#, use: "on this Mac"),
        RetiredWord(
            name: "endpoint",
            pattern: [
                #"\bAI endpoints?\b"#, #"\b[Cc]ustom endpoints?\b"#, #"OpenAI-compatible endpoints?"#,
                #"\b[Ee]ndpoint URLs?\b"#, #"\b[Bb]ase URLs?\b"#,
            ].joined(separator: "|"),
            use: "another AI service, or server address"),
        RetiredWord(name: "az login", pattern: #"\baz login\b"#, use: "Azure CLI sign-in"),
        RetiredWord(name: "CLI", pattern: #"\bCLI\b"#, use: "command-line tool, after the product name"),
        RetiredWord(name: "licence", pattern: #"\b[Ll]icen[cs]es?\b"#, use: "subscription"),
        RetiredWord(
            name: "execution provider",
            pattern: #"\b[Ee]xecution providers?\b|\b[Hh]ardware runtimes?\b"#,
            use: "AI runtime for this Mac"),
        RetiredWord(name: "DPAPI", pattern: #"\bDPAPI\b"#, use: "saved encrypted in your Keychain"),
        RetiredWord(
            name: "prompt",
            pattern: #"\b(?:[Ff]rontier|[Ll]ocal|[Ss]ystem|[Cc]leanup|[Ss]tyle) prompts?\b|\b[Pp]rompt styles?\b"#,
            use: "detailed instructions, or short instructions"),
        RetiredWord(name: "raw text", pattern: #"\b[Rr]aw (?:text|transcripts?)\b"#, use: "what Scribe heard"),
        RetiredWord(name: "glossary", pattern: #"\b[Gg]lossar(?:y|ies)\b"#, use: "vocabulary"),
        RetiredWord(name: "library", pattern: #"\b[Ll]ibrar(?:y|ies)\b"#, use: "word pack"),
        RetiredWord(
            name: "trigger phrase",
            pattern: #"\b[Tt]rigger phrases?\b|\b[Ee]xpands to\b"#,
            use: "When you say, and Scribe types"),
        RetiredWord(name: "process name", pattern: #"\b[Pp]rocess names?\b"#, use: "apps"),
        RetiredWord(name: "playground", pattern: #"\b[Pp]layground\b|\b[Tt]est box\b"#, use: "Try dictation"),
        RetiredWord(
            name: "text insertion",
            pattern: #"\b[Tt]ext insertion\b|\b[Ii]njection\b"#,
            use: "typing into apps"),
        RetiredWord(
            name: "insertion choice",
            pattern: #"\bType it in\b|\bPaste it in\b"#,
            use: "Type the text, or Paste the text"),
        RetiredWord(
            name: "terminal",
            pattern: #"\bterminals?\b|\bTerminals\b|\b[Ss]hells?\b"#,
            use: "command windows, such as Terminal"),
        RetiredWord(name: "insight", pattern: #"\b[Ii]nsights?\b"#, use: "AI summary"),
        RetiredWord(name: "log bundle", pattern: #"\b[Ll]og bundles?\b"#, use: "diagnostics"),
        RetiredWord(name: "Settings saved", pattern: #"\bSettings saved\b"#, use: "Changes saved"),
        RetiredWord(name: "Forever", pattern: #"\bForever\b"#, use: "Until I delete them"),
        RetiredWord(name: "No unsaved changes", pattern: #"\bNo unsaved changes\b"#, use: "All changes saved"),
    ]

    /// Names that are right on Windows and wrong on a Mac.
    static let macRules: [RetiredWord] = [
        RetiredWord(name: "PC", pattern: #"\bPCs?\b"#, use: "Mac"),
        RetiredWord(name: "Windows", pattern: #"\bWindows\b"#, use: "macOS"),
        RetiredWord(name: "tray", pattern: #"\b[Tt]ray\b"#, use: "menu bar"),
        RetiredWord(name: "File Explorer", pattern: #"\bFile Explorer\b"#, use: "Finder"),
        RetiredWord(name: "Microsoft Store", pattern: #"\bMicrosoft Store\b"#, use: "the Mac"),
        RetiredWord(name: "Ctrl and Alt", pattern: #"\bCtrl\b|\bAlt\b"#, use: "Control, Option"),
    ]

    /// The retired words `text` uses, after the product names that keep their own words are set aside.
    static func violations(in text: String) -> [RetiredWord] {
        var cleaned = text
        for product in productNames {
            cleaned = replacing(product, in: cleaned, with: " ")
        }
        return retired.filter { word in
            guard let regex = try? NSRegularExpression(pattern: word.pattern) else { return false }
            return regex.firstMatch(in: cleaned, range: NSRange(cleaned.startIndex..., in: cleaned)) != nil
        }
    }

    private static func replacing(_ pattern: String, in text: String, with replacement: String) -> String {
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return text }
        let range = NSRange(text.startIndex..., in: text)
        return regex.stringByReplacingMatches(in: text, range: range, withTemplate: replacement)
    }
}
