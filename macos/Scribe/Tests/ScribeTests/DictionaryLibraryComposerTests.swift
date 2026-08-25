import XCTest
@testable import Scribe

final class DictionaryLibraryComposerTests: XCTestCase {
    func testComposeLibrariesFlattensOnlyEnabledEntries() {
        let libraryA = DictionaryLibrary(
            id: "a", name: "A", category: "Cat", description: nil, builtIn: true,
            entries: [
                DictionaryEntry(pattern: "foo", replacement: "Foo"),
                DictionaryEntry(pattern: "disabled", replacement: "Nope", enabled: false),
            ])
        let libraryB = DictionaryLibrary(
            id: "b", name: "B", category: "Cat", description: nil, builtIn: true,
            entries: [DictionaryEntry(pattern: "bar", replacement: "Bar")])

        let composed = DictionaryLibraryComposer.composeLibraries([libraryA, libraryB])

        XCTAssertEqual(composed.map(\.pattern), ["foo", "bar"])
    }

    func testComposeLibrariesDeduplicatesByTrimmedCaseInsensitivePattern() {
        let libraryA = DictionaryLibrary(
            id: "a", name: "A", category: "Cat", description: nil, builtIn: true,
            entries: [DictionaryEntry(pattern: "Azure", replacement: "Azure")])
        let libraryB = DictionaryLibrary(
            id: "b", name: "B", category: "Cat", description: nil, builtIn: true,
            entries: [DictionaryEntry(pattern: " azure ", replacement: "Azure Cloud")])

        let composed = DictionaryLibraryComposer.composeLibraries([libraryA, libraryB])

        XCTAssertEqual(composed.count, 1)
        XCTAssertEqual(composed[0].replacement, "Azure") // first occurrence wins
    }

    func testMergePrefersBaseEntriesOverLibraryEntriesOnConflict() {
        let base = [DictionaryEntry(pattern: "github", replacement: "GitHub (mine)")]
        let library = [DictionaryEntry(pattern: "github", replacement: "GitHub"), DictionaryEntry(pattern: "azure", replacement: "Azure")]

        let merged = DictionaryLibraryComposer.merge(baseEntries: base, libraryEntries: library)

        XCTAssertEqual(merged.count, 2)
        XCTAssertEqual(merged[0].replacement, "GitHub (mine)")
        XCTAssertEqual(merged[1].pattern, "azure")
    }

    func testMergeWithNoLibraryEntriesReturnsBaseUnchanged() {
        let base = [DictionaryEntry(pattern: "foo", replacement: "Foo")]

        let merged = DictionaryLibraryComposer.merge(baseEntries: base, libraryEntries: [])

        XCTAssertEqual(merged, base)
    }

    func testBlankPatternsAreDropped() {
        let composed = DictionaryLibraryComposer.merge(
            baseEntries: [DictionaryEntry(pattern: "  ", replacement: "x")], libraryEntries: [])

        XCTAssertTrue(composed.isEmpty)
    }
}
