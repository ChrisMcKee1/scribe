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
    ) -> RuleSetRefresher {
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
}
