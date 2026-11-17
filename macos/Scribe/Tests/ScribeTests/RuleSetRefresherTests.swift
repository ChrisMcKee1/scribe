import XCTest

@testable import Scribe

/// Applied rule sets, recorded on the main actor.
@MainActor
final class StorageTestRuleSink {
    private(set) var applied: [PersistenceRuleSet] = []
    private(set) var failures = 0

    func apply(_ rules: PersistenceRuleSet) {
        applied.append(rules)
    }

    func fail() {
        failures += 1
    }
}

final class RuleSetRefresherTests: XCTestCase {
    private static func rules(_ pattern: String) -> PersistenceRuleSet {
        PersistenceRuleSet(
            dictionaryEntries: [DictionaryEntry(id: 1, pattern: pattern, replacement: pattern.uppercased())],
            snippets: [],
            appProfiles: [])
    }

    @MainActor
    private func makeRefresher(
        _ sink: StorageTestRuleSink,
        load: @escaping @Sendable () async throws -> PersistenceRuleSet
    ) -> RuleSetRefresher<PersistenceRuleSet> {
        RuleSetRefresher(
            load: load,
            apply: { rules in sink.apply(rules) },
            onFailure: { _ in sink.fail() })
    }

    @MainActor
    func testAnOlderRefreshThatFinishesLastIsNotApplied() async {
        let gates = StorageTestCallGates(count: 2)
        let sink = StorageTestRuleSink()
        let refresher = makeRefresher(sink) {
            let call = await gates.pass()
            return Self.rules(call == 0 ? "older" : "newer")
        }

        let older = Task { await refresher.refresh() }
        await gates.gate(0).waitForArrival()
        let newer = Task { await refresher.refresh() }
        await gates.gate(1).waitForArrival()

        await gates.gate(1).open()
        let newerOutcome = await newer.value
        await gates.gate(0).open()
        let olderOutcome = await older.value

        XCTAssertEqual(newerOutcome, .applied)
        XCTAssertEqual(olderOutcome, .superseded)

        XCTAssertEqual(sink.applied, [Self.rules("newer")])
        XCTAssertEqual(sink.failures, 0)
    }

    @MainActor
    func testAFailedRefreshKeepsTheRulesAlreadyInUse() async {
        let attempts = SettingsTestCounter()
        let sink = StorageTestRuleSink()
        let refresher = makeRefresher(sink) {
            let attempt = await attempts.count
            await attempts.increment()
            if attempt > 0 {
                throw StorageTestFailure(message: "the rules could not be read")
            }
            return Self.rules("first")
        }

        await refresher.refresh()
        await refresher.refresh()

        XCTAssertEqual(sink.applied, [Self.rules("first")])
        XCTAssertEqual(sink.failures, 1)
    }

    @MainActor
    func testAnOlderFailureThatArrivesAfterANewerSuccessIsIgnored() async {
        let gates = StorageTestCallGates(count: 2)
        let sink = StorageTestRuleSink()
        let refresher = makeRefresher(sink) {
            let call = await gates.pass()
            if call == 0 {
                throw StorageTestFailure(message: "the older read failed")
            }
            return Self.rules("newer")
        }

        let older = Task { await refresher.refresh() }
        await gates.gate(0).waitForArrival()
        let newer = Task { await refresher.refresh() }
        await gates.gate(1).waitForArrival()

        await gates.gate(1).open()
        let newerOutcome = await newer.value
        await gates.gate(0).open()
        let olderOutcome = await older.value

        XCTAssertEqual(newerOutcome, .applied)
        XCTAssertEqual(olderOutcome, .superseded)

        XCTAssertEqual(sink.applied, [Self.rules("newer")])
        XCTAssertEqual(sink.failures, 0)
    }

    /// The app compiles the rules off the main actor (`DictationRuleSnapshot.compile`) and installs the result in one
    /// step: a dictation meets the old rules until the install, and all of the new ones afterwards, profiles included.
    @MainActor
    func testACompiledSnapshotReplacesTheRulesInOneStep() async {
        let rules = DictationRules(gate: StartupGate())
        rules.apply(Self.rules("cube flow"), libraryEntries: [])
        let profile = AppProfile(
            name: "Editor", bundleIdentifiers: ["com.example.editor"], processNames: [], writingStylePrompt: nil,
            newlineHandling: nil)
        let next = PersistenceRuleSet(
            dictionaryEntries: [DictionaryEntry(id: 2, pattern: "azure", replacement: "Azure")],
            snippets: [Snippet(phrase: "sign off", template: "Pat\nDoe")],
            appProfiles: [profile])
        let library = [DictionaryEntry(pattern: "kube", replacement: "Kube")]

        let snapshot = await DictationRuleSnapshot.compile(
            next,
            libraryEntries: library,
            cleanupVocabularyEntries: library,
            aiScope: .none)
        XCTAssertEqual(rules.postProcess("cube flow on azure").text, "CUBE FLOW on azure")
        XCTAssertTrue(rules.appProfiles.isEmpty)

        rules.install(snapshot)

        XCTAssertEqual(rules.postProcess("cube flow on azure then sign off").text, "cube flow on Azure then Pat\nDoe")
        XCTAssertEqual(rules.appProfiles.map(\.name), ["Editor"])
        XCTAssertEqual(snapshot.dictionaryEntryCount, 1)
        XCTAssertEqual(snapshot.libraryEntryCount, 1)
        XCTAssertEqual(snapshot.snippetCount, 1)
    }

    /// The app compiles each load of the word packs together with the permission it was read under
    /// (`DictationRuleSnapshot.compile(_:vocabulary:)`, which the rule refresher calls), and installing the snapshot
    /// hands that permission to the dictations that follow, which hold every cleanup request to it. A scope dropped on
    /// the way would leave every request unheld, whatever a save withdraws.
    @MainActor
    func testACompiledSnapshotCarriesThePermissionItsVocabularyWasReadUnderToTheInstalledRules() async {
        let rules = DictationRules(gate: StartupGate())
        let entry = DictionaryEntry(pattern: "kube", replacement: "Kube")
        let scope = AiVocabularyScope(generation: 4, permittedContent: ["team": "h1"])
        let vocabulary = LibraryVocabulary(generation: 4, entries: [entry], aiEntries: [entry], aiScope: scope)

        let snapshot = await DictationRuleSnapshot.compile(Self.rules("cube flow"), vocabulary: vocabulary)
        XCTAssertEqual(rules.cleanupVocabulary.admission.scope, .none, "the scope was in force before the install")

        rules.install(snapshot)

        XCTAssertEqual(snapshot.cleanupVocabulary.admission.scope, scope)
        XCTAssertEqual(rules.cleanupVocabulary.admission.scope, scope, "the install dropped the permission")
        XCTAssertEqual(snapshot.libraryEntryCount, 1)
    }
}
