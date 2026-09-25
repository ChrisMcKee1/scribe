import XCTest

@testable import Scribe

/// What building the compiled rules costs with every built-in library switched on and twenty snippets, and what the
/// main actor pays to install them. The app compiles whenever the rules change, off the main actor
/// (`DictationRuleSnapshot.compile`, from `RuleSetRefresher`), and installs the result on it in one step
/// (`DictationRules.install`). The times are reported in the job summary, never asserted: runner speed varies too much
/// for a bound to mean anything. One dictation's rule work with all of those rules is reported beside them, with AI
/// cleanup off and on, and a model that changes nothing must still get exactly the cleanup-off text.
@MainActor
final class RuleReloadScenarioTests: XCTestCase {
    private static let builds = 7

    func testTheCostOfARulesReloadWithEveryLibraryAndTwentySnippets() async {
        let libraries = BuiltInDictionaryLibraries.all
        XCTAssertFalse(libraries.isEmpty, "no built-in library was found")
        let libraryEntries = libraries.flatMap(\.entries).filter(\.enabled)
        let snippets = (1...20).map { index in
            Snippet(phrase: "insert block \(index)", template: "Block \(index)\nsecond line of block \(index)")
        }
        let dictionary = [
            DictionaryEntry(pattern: "cube flow", replacement: "Kubeflow"),
            DictionaryEntry(pattern: "my sign off", replacement: "Pat Doe\nSupport lead"),
        ]
        let ruleSet = PersistenceRuleSet(dictionaryEntries: dictionary, snippets: snippets, appProfiles: [])
        let clock = ContinuousClock()

        var builds: [Duration] = []
        for _ in 0..<Self.builds {
            let started = clock.now
            _ = DictationRuleSnapshot(ruleSet, libraryEntries: libraryEntries)
            builds.append(started.duration(to: clock.now))
        }
        let snapshot = await DictationRuleSnapshot.compile(ruleSet, libraryEntries: libraryEntries)
        let rules = DictationRules(gate: StartupGate())
        var started = clock.now
        rules.install(snapshot)
        let installDuration = started.duration(to: clock.now)

        let transcript =
            "please insert block 7 and deploy it with cube flow to azure kubernetes service on github then my sign off"
        started = clock.now
        let cleanupOff = rules.postProcess(transcript)
        let offDuration = started.duration(to: clock.now)
        started = clock.now
        let pass = rules.correctVocabulary(transcript)
        let beforeDuration = started.duration(to: clock.now)
        started = clock.now
        let finished = rules.finishAfterCleanup(pass.text, after: pass)
        let afterDuration = started.duration(to: clock.now)
        XCTAssertEqual(finished.text, cleanupOff.text)
        XCTAssertFalse(pass.text.contains("second line"), "a snippet template was sent")
        XCTAssertFalse(pass.text.contains("Pat Doe"), "a template-like replacement was sent")

        let sorted = builds.sorted()
        let report = ScenarioReport("rules-reload")
        report.note("libraries", count: libraries.count)
        report.note("libraryEntries", count: libraryEntries.count)
        report.note("snippets", count: snippets.count)
        report.note("firstBuild", duration: builds[0])
        report.note("medianBuild", duration: sorted[sorted.count / 2])
        report.note("slowestBuild", duration: sorted[sorted.count - 1])
        report.note("offMainBuild", duration: snapshot.compileDuration)
        report.note("mainActorInstall", duration: installDuration)
        report.note("dictationCleanupOff", duration: offDuration)
        report.note("dictationBeforeCleanup", duration: beforeDuration)
        report.note("dictationAfterCleanup", duration: afterDuration)
        report.write()
    }
}
