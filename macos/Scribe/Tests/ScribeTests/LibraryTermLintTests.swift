import XCTest

@testable import Scribe

final class LibraryTermLintTests: XCTestCase {
    func testRepresentativeHintsMatchWindowsBehavior() {
        XCTAssertEqual(LibraryTermLint.check(TermValues("il", "IL", true)), [.ordinaryWord])
        XCTAssertEqual(LibraryTermLint.check(TermValues(" Di ", "DI", true)), [.ordinaryWord, .irregularSpacing])
        XCTAssertEqual(LibraryTermLint.check(TermValues("and", "AND", true)), [])
        XCTAssertEqual(LibraryTermLint.check(TermValues("sol", "SOL", false)), [.wholeWordOff])
        XCTAssertEqual(LibraryTermLint.check(TermValues("Distillation", "distillation", true)), [.forcesLowercase])
        XCTAssertEqual(LibraryTermLint.check(TermValues("sig", "Line one\nLine two", true)), [.multiLine])
        XCTAssertEqual(
            LibraryTermLint.check(
                TermValues(
                    "sig",
                    String(repeating: "a", count: CleanupPrompt.maxGlossaryTermChars + 1),
                    true)),
            [.longForGlossary])
    }

    func testShippedBuiltInRowsRaiseNoHints() {
        let hinted = BuiltInDictionaryLibraries.all.flatMap { library in
            library.entries.compactMap { entry -> String? in
                let hints = LibraryTermLint.check(TermValues(entry: entry))
                return hints.isEmpty ? nil : "\(library.id): \(entry.pattern)"
            }
        }

        XCTAssertTrue(hinted.isEmpty, hinted.joined(separator: ", "))
    }
}
