import XCTest

@testable import Scribe

final class LibraryNamingTests: XCTestCase {
    func testSlugFixture() throws {
        let fixture = try JSONDecoder().decode(SlugFixture.self, from: LibraryFixtureSupport.json("slugs.json"))

        for item in fixture.slugs {
            XCTAssertEqual(LibraryNaming.slug(item.name), item.slug)
        }
    }

    func testNewCustomIDFixture() throws {
        let fixture = try JSONDecoder().decode(SlugFixture.self, from: LibraryFixtureSupport.json("slugs.json"))

        for item in fixture.newCustomIDs {
            XCTAssertEqual(LibraryNaming.newCustomID(name: item.name, takenIDs: item.taken), item.id)
        }
    }

    func testRemapIDFixture() throws {
        let fixture = try JSONDecoder().decode(SlugFixture.self, from: LibraryFixtureSupport.json("slugs.json"))

        for item in fixture.remapIDs {
            XCTAssertEqual(LibraryNaming.remapID(stem: item.stem, takenIDs: item.taken), item.id)
        }
    }
}

private struct SlugFixture: Decodable {
    let slugs: [SlugCase]
    let newCustomIDs: [NewCustomIDCase]
    let remapIDs: [RemapIDCase]

    struct SlugCase: Decodable {
        let name: String
        let slug: String
    }

    struct NewCustomIDCase: Decodable {
        let name: String
        let taken: [String]
        let id: String
    }

    struct RemapIDCase: Decodable {
        let stem: String
        let taken: [String]
        let id: String
    }

    private enum CodingKeys: String, CodingKey {
        case slugs
        case newCustomIDs = "newCustomIds"
        case remapIDs = "remapIds"
    }
}
