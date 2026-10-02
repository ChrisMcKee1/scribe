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

        for scenario in GoldenFixture.supportedScenarios {
            fixture.service.settings.enabledLibraryIds = Set(scenario.enabledIds)
            let catalog = try await fixture.service.loadCatalog()
            let rules = Self.composeRules(from: catalog)
            let effective = Self.effectiveWinners(dictionary: GoldenFixture.sortedPersonalEntries, rules: rules)
            let processor = TextPostProcessor()
            processor.reload(
                dictionaryEntries: GoldenFixture.sortedPersonalEntries.filter(\.enabled),
                snippets: [],
                libraryEntries: rules.map(\.entry))

            let enabled = try golden.requiredSection("\(scenario.name): enabled libraries in composition order")
            XCTAssertEqual([Self.enabledLibraryIDs(in: catalog).joined(separator: ", ")], enabled, scenario.name)
            let libraryWinners = try golden.requiredSection(
                "\(scenario.name): library winners for the fixture's spoken forms, in library composition order")
            XCTAssertEqual(Self.describeLibraryWinners(rules), libraryWinners.map(Self.stripNumbering), scenario.name)
            let effectiveWinners = try golden.requiredSection(
                "\(scenario.name): effective winners for the fixture's spoken forms, in effective order")
            XCTAssertEqual(effective, effectiveWinners, scenario.name)
            XCTAssertEqual(
                GoldenFixture.sentences.map { "\($0) => \(processor.processDetailed($0).text)" },
                try golden.requiredSection("\(scenario.name): finished text from the post-processor"),
                scenario.name)
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

    private static func composeRules(from catalog: LibraryCatalog) -> [ComposedLibraryRule] {
        let activeLibraries = catalog.libraries.filter { library in
            catalog.localState.enabledIdSet.contains(library.id.lowercased())
                && (library.state == .available || library.state == .partlyReadable)
        }
        var byTier: [RuleTier: [ComposedLibraryRule]] = [.authored: [], .shipped: [], .legacy: []]

        for library in activeLibraries {
            for entry in library.library.entries where entry.enabled {
                let key = LibraryTermKey.from(entry.pattern)
                guard !key.isEmpty else { continue }
                let tier: RuleTier
                if library.library.legacyMarkedKeys.contains(key) {
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
        return rules.filter { GoldenFixture.fixtureKeys.contains($0.key.value) }
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

    private static func describeLibraryWinners(_ rules: [ComposedLibraryRule]) -> [String] {
        rules.map { rule in
            "\(rule.entry.pattern) => \(rule.entry.replacement) [\(rule.libraryId)]"
        }
    }

    private static func stripNumbering(_ line: String) -> String {
        guard line.hasPrefix("#") else {
            return line
        }
        return line.replacing(/#[0-9]+\s+/, with: "")
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
