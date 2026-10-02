import XCTest

@testable import Scribe

final class LibraryCompositionGoldenTests: XCTestCase {
    func testLibraryOrderMatchesGoldenFixture() throws {
        let fixture = try GoldenFixture.make()
        defer { fixture.cleanup() }

        let golden = try GoldenSections.load()
        XCTAssertEqual(
            fixture.service.libraries().map(Self.describeLibrary),
            try golden.requiredSection("libraries, in the order GetLibraries returns them"))
    }

    func testSelectedGoldenSectionsMatchCurrentComposition() async throws {
        let fixture = try GoldenFixture.make()
        defer { fixture.cleanup() }
        let golden = try GoldenSections.load()
        var actual: [String: [String]] = [
            "libraries, in the order GetLibraries returns them": fixture.service.libraries().map(Self.describeLibrary)
        ]

        for scenario in GoldenFixture.scenarios {
            fixture.service.settings.enabledLibraryIds = Set(scenario.enabledIds)
            let catalog = try await fixture.service.loadCatalog()
            let rules = Self.composeRules(from: catalog, fixtureOnly: true)
            let allRules = Self.composeRules(from: catalog, fixtureOnly: false)
            let effective = Self.effectiveWinners(dictionary: GoldenFixture.sortedPersonalEntries, rules: rules)
            let processor = TextPostProcessor()
            processor.reload(
                dictionaryEntries: GoldenFixture.sortedPersonalEntries.filter(\.enabled),
                snippets: [],
                libraryEntries: allRules.map(\.entry))

            actual["\(scenario.name): enabled libraries in composition order"] = [
                Self.enabledLibraryIDs(in: catalog).joined(separator: ", ")
            ]
            actual["\(scenario.name): library winners for the fixture's spoken forms, in library composition order"] =
                Self.describeLibraryWinners(rules, numbered: scenario.name == "custom only")
            actual["\(scenario.name): effective winners for the fixture's spoken forms, in effective order"] = effective
            actual["\(scenario.name): Dictionary page library badges (what covers each personal entry)"] =
                Self.badges(dictionary: GoldenFixture.sortedPersonalEntries, rules: allRules)
            actual["\(scenario.name): Save prompt report"] =
                Self.savePrompt(dictionary: GoldenFixture.sortedPersonalEntries, rules: allRules)
            actual["\(scenario.name): finished text from the post-processor"] =
                GoldenFixture.sentences.map { "\($0) => \(processor.processDetailed($0).text)" }

            let glossary = Self.glossarySources(dictionary: GoldenFixture.sortedPersonalEntries, rules: allRules)
            switch scenario.name {
            case "custom only":
                actual["custom only: every effective rule"] = effective
                actual["custom only: AI cleanup glossary, on-device"] = [
                    CleanupPrompt.buildGlossary(
                        glossary.entries.map(\.entry), maxTerms: CleanupPrompt.maxGlossaryTermsLocal)
                ].flatMap { $0.components(separatedBy: .newlines) }
                actual["custom only: AI cleanup glossary, cloud"] = [
                    CleanupPrompt.buildGlossary(
                        glossary.entries.map(\.entry), maxTerms: CleanupPrompt.maxGlossaryTermsCloud)
                ].flatMap { $0.components(separatedBy: .newlines) }
                actual["custom only: AI cleanup glossary, cut at 6 terms"] = [
                    CleanupPrompt.buildGlossary(glossary.entries.map(\.entry), maxTerms: 6)
                ].flatMap { $0.components(separatedBy: .newlines) }
                actual["custom only: sources of the first 80 effective rules"] =
                    [Self.sourceCounts(glossary.entries.prefix(80).map(\.source))]
            case "default install":
                actual["default install: sources of the first 80 effective rules"] =
                    [Self.sourceCounts(glossary.entries.prefix(80).map(\.source))]
            case "shipped and custom", "everything":
                actual["\(scenario.name): sources of the on-device glossary's 80 lines"] =
                    [Self.sourceCounts(glossary.entries.prefix(80).map(\.source))]
                actual[
                    "\(scenario.name): shipped terms displaced from the on-device glossary's 80 lines, by shipped spoken form"
                ] =
                    [
                        glossary.entries.dropFirst(80).prefix(5).map(\.entry.pattern).joined(separator: ", ")
                    ]
                if scenario.name == "everything" {
                    let included = glossary.entries.prefix(glossary.cloudIncluded)
                    let cut = glossary.entries.dropFirst(glossary.cloudIncluded)
                    actual["everything: sources of the cloud glossary's included lines"] =
                        [Self.sourceCounts(included.map(\.source))]
                    actual["everything: eligible lines past the cloud glossary's 24,000 characters"] =
                        ["\(cut.count) of \(glossary.entries.count) eligible lines cut"]
                    actual["everything: lines past the cloud glossary's 24,000 characters, by library"] =
                        [Self.sourceCounts(cut.map(\.source))]
                }
            default:
                break
            }
        }

        XCTAssertEqual(Set(actual.keys), Set(golden.keys))
        for section in golden.keys.sorted() {
            XCTAssertEqual(actual[section], golden[section], section)
        }
    }

    private static func describeLibrary(_ library: DictionaryLibrary) -> String {
        let kind = library.builtIn ? "built-in" : "custom"
        return "\(library.id) (\(kind)) \"\(library.name)\""
    }

    private static func enabledLibraryIDs(in catalog: LibraryCatalog) -> [String] {
        catalog.libraries.filter { library in
            catalog.localState.enabledIdSet.contains(library.id.lowercased())
                && (library.state == .available || library.state == .partlyReadable)
        }.map(\.id)
    }

    private static func composeRules(from catalog: LibraryCatalog, fixtureOnly: Bool) -> [ComposedLibraryRule] {
        let activeLibraries = catalog.libraries.filter { library in
            catalog.localState.enabledIdSet.contains(library.id.lowercased())
                && (library.state == .available || library.state == .partlyReadable)
        }
        var activeBuiltInFolds = Set<String>()
        for library in activeLibraries where library.builtIn {
            for entry in library.library.entries where entry.enabled {
                activeBuiltInFolds.insert(DictionaryLibraryService.markerFold(entry.pattern))
            }
        }
        var byTier: [RuleTier: [ComposedLibraryRule]] = [.authored: [], .shipped: [], .legacy: []]

        for library in activeLibraries {
            for entry in library.library.entries where entry.enabled {
                let key = LibraryTermKey.from(entry.pattern)
                guard !key.isEmpty else { continue }
                let tier: RuleTier
                if library.library.legacyMarkedKeys.contains(key),
                    activeBuiltInFolds.contains(DictionaryLibraryService.markerFold(entry.pattern))
                {
                    tier = .legacy
                } else if !library.builtIn || library.library.authoredKeys.contains(key) {
                    tier = .authored
                } else {
                    tier = .shipped
                }
                byTier[tier, default: []].append(
                    ComposedLibraryRule(entry: entry, libraryId: library.id, key: key, tier: tier))
            }
        }

        var seen = Set<LibraryTermKey>()
        var rules: [ComposedLibraryRule] = []
        for tier in [RuleTier.authored, .shipped, .legacy] {
            for rule in byTier[tier, default: []] where seen.insert(rule.key).inserted {
                rules.append(rule)
            }
        }
        return fixtureOnly ? rules.filter { GoldenFixture.fixtureKeys.contains($0.key.value) } : rules
    }

    private static func effectiveWinners(dictionary: [DictionaryEntry], rules: [ComposedLibraryRule]) -> [String] {
        var seen = Set<LibraryTermKey>()
        var lines: [String] = []

        for entry in dictionary where entry.enabled {
            let key = LibraryTermKey.from(entry.pattern)
            guard GoldenFixture.fixtureKeys.contains(key.value), seen.insert(key).inserted else { continue }
            lines.append("\(entry.pattern) => \(entry.replacement) [your dictionary]")
        }
        for rule in rules where seen.insert(rule.key).inserted {
            lines.append("\(rule.entry.pattern) => \(rule.entry.replacement) [\(rule.libraryId)]")
        }
        return lines
    }

    private static func describeLibraryWinners(_ rules: [ComposedLibraryRule], numbered: Bool) -> [String] {
        rules.enumerated().map { index, rule in
            let line = "\(rule.entry.pattern) => \(rule.entry.replacement) [\(rule.libraryId)]"
            return numbered ? "#\(index + 1) \(line)" : line
        }
    }

    private static func badges(dictionary: [DictionaryEntry], rules: [ComposedLibraryRule]) -> [String] {
        var ruleByKey: [String: ComposedLibraryRule] = [:]
        var libraryNames: [String: String] = [:]
        for rule in rules {
            ruleByKey[rule.key.value] = ruleByKey[rule.key.value] ?? rule
            libraryNames[rule.libraryId] = libraryNames[rule.libraryId] ?? Self.libraryName(rule.libraryId)
        }
        return dictionary.map { entry in
            let key = LibraryTermKey.from(entry.pattern).value
            guard let rule = ruleByKey[key] else {
                return "\(entry.pattern): not covered"
            }
            return
                "\(entry.pattern): \"\(libraryNames[rule.libraryId] ?? rule.libraryId)\" writes \"\(rule.entry.replacement)\""
        }
    }

    private static func savePrompt(dictionary: [DictionaryEntry], rules: [ComposedLibraryRule]) -> [String] {
        var ruleByKey: [String: ComposedLibraryRule] = [:]
        for rule in rules {
            ruleByKey[rule.key.value] = ruleByKey[rule.key.value] ?? rule
        }
        var rows: [(DictionaryOverlapKind, DictionaryEntry, ComposedLibraryRule)] = []
        for entry in dictionary where entry.enabled {
            let key = LibraryTermKey.from(entry.pattern).value
            guard let rule = ruleByKey[key] else { continue }
            let kind: DictionaryOverlapKind =
                entry.replacement == rule.entry.replacement && entry.wholeWord == rule.entry.wholeWord
                ? .redundant : .override
            rows.append((kind, entry, rule))
        }
        let redundant = rows.count { $0.0 == .redundant }
        let override = rows.count { $0.0 == .override }
        var lines = ["\(redundant) redundant, \(override) override"]
        lines += rows.map { kind, entry, rule in
            let prefix = kind == .redundant ? "Redundant" : "Override"
            return
                "\(prefix) \(entry.pattern) -> \"\(entry.replacement)\", library writes \"\(rule.entry.replacement)\", named \"\(libraryName(rule.libraryId))\""
        }
        return lines
    }

    private static func glossarySources(
        dictionary: [DictionaryEntry],
        rules: [ComposedLibraryRule]
    ) -> (entries: [(entry: DictionaryEntry, source: String)], cloudIncluded: Int) {
        var candidates: [(DictionaryEntry, String)] = []
        var seenPatterns = Set<String>()
        for entry in dictionary where entry.enabled {
            let key = LibraryTermKey.from(entry.pattern).value
            guard seenPatterns.insert(key).inserted else { continue }
            candidates.append((entry, "your dictionary"))
        }
        for rule in rules {
            guard seenPatterns.insert(rule.key.value).inserted else { continue }
            candidates.append((rule.entry, rule.libraryId))
        }
        var seen = Set<String>()
        var entries: [(DictionaryEntry, String)] = []
        var characters = 0
        var cloudIncluded = 0
        var cloudStopped = false
        for (entry, source) in candidates where TextPostProcessor.isVocabulary(entry) {
            let canonical = CleanupPrompt.normalizeTerm(entry.replacement)
            guard !canonical.isEmpty else { continue }
            let spoken = CleanupPrompt.normalizeTerm(entry.pattern)
            let key =
                !spoken.isEmpty && spoken.caseInsensitiveCompare(canonical) != .orderedSame
                ? "\(canonical)|\(spoken)" : canonical
            guard seen.insert(key.lowercased()).inserted else { continue }
            let line = CleanupPrompt.glossaryLine(
                canonical: canonical,
                spoken: key.contains("|") ? spoken : nil)
            if !cloudStopped && characters + line.utf16.count + 1 <= CleanupPrompt.maxGlossaryChars {
                cloudIncluded += 1
                characters += line.utf16.count + 1
            } else {
                cloudStopped = true
            }
            entries.append((entry, source))
        }
        return (entries, cloudIncluded)
    }

    private static func sourceCounts(_ sources: [String]) -> String {
        var counts: [(String, Int)] = []
        for source in sources {
            if counts.last?.0 == source {
                counts[counts.count - 1].1 += 1
            } else {
                counts.append((source, 1))
            }
        }
        return counts.map { "\($0.0) x\($0.1)" }.joined(separator: ", ")
    }

    private static func libraryName(_ id: String) -> String {
        if let fixture = GoldenFixture.customFiles.first(where: {
            URL(fileURLWithPath: $0.fileName).deletingPathExtension().lastPathComponent.caseInsensitiveCompare(id)
                == .orderedSame
        }) {
            return DictionaryLibraryCsv.parse(fixture.csv).name ?? id
        }
        return BuiltInDictionaryLibraries.all.first { $0.id.caseInsensitiveCompare(id) == .orderedSame }?.name ?? id
    }
}

private struct GoldenFixture {
    let directory: StorageTestDirectory
    let defaults: StorageTestDefaults
    let service: DictionaryLibraryService

    static let customFiles: [GoldenFixtureFile] = [
        GoldenFixtureFile(
            fileName: "team-terms.csv",
            csv: """
                # name: Team terms
                # category: Custom
                pattern,replacement,whole_word,enabled
                get hub,GitHub Enterprise,true,true
                kube,Kubernetes,true,true
                north star,North Star,true,true
                contoso,Contoso Ltd,true,true
                pipeline,Pipelines,true,true
                """),
        GoldenFixtureFile(
            fileName: "team-terms-2.csv",
            csv: """
                # name: Team terms v2
                # category: Custom
                pattern,replacement,whole_word,enabled
                kube,K8s,true,true
                north star,NorthStar,true,true
                pipeline,Pipeline,true,false
                """),
        GoldenFixtureFile(
            fileName: "alpha.csv",
            csv: """
                # name: Zeta words
                pattern,replacement
                contoso,CONTOSO
                fabrikam,Fabrikam
                """),
        GoldenFixtureFile(
            fileName: "Zulu Notes.csv",
            csv: """
                # name: alpha notes
                pattern,replacement
                fabrikam,FabriKam
                tailspin,Tailspin Toys
                """),
        GoldenFixtureFile(
            fileName: "release-10.csv",
            csv: """
                # name: Release 10 terms
                pattern,replacement
                sprint,Sprint 10
                retro,Retro
                """),
        GoldenFixtureFile(
            fileName: "release-9.csv",
            csv: """
                # name: Release 9 terms
                pattern,replacement
                sprint,Sprint 9
                standup,Stand-up
                gpt five six terra,GPT 5.6 Terra
                """),
    ]

    static let personalEntries: [DictionaryEntry] = [
        DictionaryEntry(pattern: "fabrikam", replacement: "Fabrikam"),
        DictionaryEntry(pattern: "pipeline", replacement: "Pipelines"),
        DictionaryEntry(pattern: "contoso", replacement: "Contoso"),
        DictionaryEntry(pattern: "azure", replacement: "Azure", enabled: false),
        DictionaryEntry(pattern: "llm", replacement: "LLM"),
        DictionaryEntry(pattern: "standup", replacement: "standup"),
        DictionaryEntry(pattern: "scribe", replacement: "Scribe"),
    ]

    static let sentences = [
        "i pushed the kube fix to get hub before the sprint retro",
        "the standup covered contoso and fabrikam near north star",
        "tailspin wants the pipeline on gpt five six terra with an llm",
        "scribe typed azure for me",
    ]

    static let scenarios: [GoldenScenario] = [
        GoldenScenario(
            name: "shipped and custom",
            enabledIds: [
                "release-9", "Zulu Notes", "github", "team-terms-2", "ai-terminology", "alpha",
                "release-10", "team-terms", "ai-model-names",
            ]),
        GoldenScenario(
            name: "custom only",
            enabledIds: ["release-9", "Zulu Notes", "team-terms-2", "alpha", "release-10", "team-terms"]),
        GoldenScenario(name: "default install", enabledIds: ["ai-model-names", "ai-terminology"]),
        GoldenScenario(
            name: "everything",
            enabledIds: Array(
                customFiles
                    .map { URL(fileURLWithPath: $0.fileName).deletingPathExtension().lastPathComponent }
                    .reversed()
            ) + Array(BuiltInDictionaryLibraries.all.map(\.id).reversed())),
    ]

    static let supportedScenarios = scenarios.filter { $0.name == "custom only" || $0.name == "default install" }

    static let sortedPersonalEntries = personalEntries.sorted {
        $0.pattern.localizedCaseInsensitiveCompare($1.pattern) == .orderedAscending
    }

    static let fixtureKeys: Set<String> = Set(
        customFiles
            .flatMap { DictionaryLibraryCsv.parse($0.csv).entries.map { LibraryTermKey.from($0.pattern).value } }
            .filter { !$0.isEmpty }
            + personalEntries.map { LibraryTermKey.from($0.pattern).value }
    )

    static func make() throws -> GoldenFixture {
        let directory = try StorageTestDirectory()
        let defaults = StorageTestDefaults()
        for file in customFiles {
            try file.csv.write(
                to: directory.url.appendingPathComponent(file.fileName, isDirectory: false),
                atomically: true,
                encoding: .utf8)
        }
        let service = DictionaryLibraryService(
            librariesDirectory: directory.url,
            settings: DictionaryLibrarySettings(defaults: defaults.defaults))
        return GoldenFixture(directory: directory, defaults: defaults, service: service)
    }

    func cleanup() {
        defaults.remove()
        directory.remove()
    }
}

private struct GoldenFixtureFile {
    let fileName: String
    let csv: String
}

private struct GoldenScenario {
    let name: String
    let enabledIds: [String]
}

private enum GoldenSections {
    static func load() throws -> [String: [String]] {
        let text = try String(
            contentsOf: LibraryFixtureSupport.fixturesDirectory
                .appendingPathComponent("composition-golden.txt", isDirectory: false),
            encoding: .utf8)
        var sections: [String: [String]] = [:]
        var currentSection: String?
        var lines: [String] = []

        for rawLine in text.components(separatedBy: .newlines) {
            if rawLine.hasPrefix("[") && rawLine.hasSuffix("]") {
                if let currentSection {
                    sections[currentSection] = lines
                }
                currentSection = String(rawLine.dropFirst().dropLast())
                lines = []
                continue
            }
            guard currentSection != nil else {
                continue
            }
            if rawLine.isEmpty {
                sections[currentSection!] = lines
                currentSection = nil
                lines = []
                continue
            }

            lines.append(rawLine)
        }
        if let currentSection {
            sections[currentSection] = lines
        }
        return sections
    }
}

extension Dictionary where Key == String, Value == [String] {
    fileprivate func requiredSection(_ name: String) throws -> [String] {
        guard let lines = self[name] else {
            throw NSError(
                domain: "LibraryCompositionGoldenTests",
                code: 1,
                userInfo: [NSLocalizedDescriptionKey: name])
        }
        return lines
    }
}
