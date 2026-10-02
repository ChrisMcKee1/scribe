import XCTest

@testable import Scribe

final class LibraryTermKeyTests: XCTestCase {
    func testSharedFixtureCommitForm() throws {
        let fixture = try JSONDecoder().decode(TermKeyFixture.self, from: LibraryFixtureSupport.json("term-keys.json"))

        for item in fixture.commitForm {
            XCTAssertEqual(LibraryTermKey.normalize(item.input), item.expected, item.note)
            XCTAssertEqual(LibraryTermKey.isInCommitForm(item.input), (item.input ?? "") == item.expected, item.note)
            XCTAssertEqual(LibraryTermKey.normalize(item.expected), item.expected, item.note)
        }
    }

    func testSharedFixtureKeyValues() throws {
        let fixture = try JSONDecoder().decode(TermKeyFixture.self, from: LibraryFixtureSupport.json("term-keys.json"))

        for item in fixture.key {
            XCTAssertEqual(LibraryTermKey.from(item.input).value, item.expected, item.note)
        }
    }

    func testSharedFixtureEquality() throws {
        let fixture = try JSONDecoder().decode(TermKeyFixture.self, from: LibraryFixtureSupport.json("term-keys.json"))

        for item in fixture.same {
            let lhs = LibraryTermKey.from(item.a)
            let rhs = LibraryTermKey.from(item.b)
            XCTAssertEqual(lhs == rhs, item.same, item.note)
            XCTAssertEqual(LibraryTermKey.areSame(item.a, item.b), item.same, item.note)
            if item.same {
                XCTAssertEqual(lhs.hashValue, rhs.hashValue, item.note)
            }
        }
    }

    func testDescriptionNeverLeaksWords() {
        let text = LibraryTermKey.from("north star").description

        XCTAssertFalse(text.localizedCaseInsensitiveContains("north"))
        XCTAssertFalse(text.localizedCaseInsensitiveContains("star"))
        XCTAssertTrue(text.contains("10"))
    }
}

private struct TermKeyFixture: Decodable {
    let commitForm: [CommitCase]
    let key: [CommitCase]
    let same: [SameCase]

    struct CommitCase: Decodable {
        let input: String?
        let expected: String
        let note: String
    }

    struct SameCase: Decodable {
        let a: String
        let b: String
        let same: Bool
        let note: String
    }
}
