import XCTest
@testable import Scribe

final class DictionaryLibraryServiceTests: XCTestCase {
    private var tempDirectory: URL!
    private var defaults: StorageTestDefaults!
    private var service: DictionaryLibraryService!

    override func setUpWithError() throws {
        tempDirectory = FileManager.default.temporaryDirectory
            .appendingPathComponent("ScribeLibraryServiceTests-\(UUID().uuidString)", isDirectory: true)
        defaults = StorageTestDefaults()
        service = DictionaryLibraryService(
            librariesDirectory: tempDirectory, settings: DictionaryLibrarySettings(defaults: defaults.defaults))
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: tempDirectory)
        defaults.remove()
    }

    func testLibrariesIncludesBuiltInsWhenNoCustomFilesExist() {
        let libraries = service.libraries()

        XCTAssertEqual(libraries.count, BuiltInDictionaryLibraries.all.count)
        XCTAssertTrue(libraries.allSatisfy(\.builtIn))
    }

    func testImportWritesNormalizedCsvAndReturnsLibrary() throws {
        let csv = "# name: My Custom Terms\n# category: Custom\npattern,replacement\nfoo bar,FooBar\n"

        let library = try service.import(csv: csv, suggestedName: "unused")

        XCTAssertEqual(library.name, "My Custom Terms")
        XCTAssertEqual(library.category, "Custom")
        XCTAssertFalse(library.builtIn)
        XCTAssertEqual(library.entries.count, 1)

        let libraries = service.libraries()
        XCTAssertTrue(libraries.contains { $0.id == library.id })

        let writtenPath = tempDirectory.appendingPathComponent("\(library.id).csv")
        XCTAssertTrue(FileManager.default.fileExists(atPath: writtenPath.path))
    }

    func testImportFallsBackToSuggestedNameWhenHeaderOmitsIt() throws {
        let library = try service.import(csv: "pattern,replacement\nfoo,Foo\n", suggestedName: "my-import")

        XCTAssertEqual(library.name, "my-import")
    }

    func testImportThrowsWhenNoUsableEntries() {
        XCTAssertThrowsError(try service.import(csv: "# name: Empty\npattern,replacement\n", suggestedName: nil)) { error in
            XCTAssertTrue(error is DictionaryLibraryServiceError)
        }
    }

    func testImportGeneratesUniqueIdWhenNameCollides() throws {
        let first = try service.import(csv: "pattern,replacement\nfoo,Foo\n", suggestedName: "Same Name")
        let second = try service.import(csv: "pattern,replacement\nbar,Bar\n", suggestedName: "Same Name")

        XCTAssertNotEqual(first.id, second.id)
    }

    func testRemoveDeletesCustomLibraryFile() throws {
        let library = try service.import(csv: "pattern,replacement\nfoo,Foo\n", suggestedName: "removable")
        service.settings.setEnabled(true, id: library.id)
        XCTAssertTrue(service.libraries().contains { $0.id == library.id })

        try service.remove(id: library.id)

        XCTAssertFalse(service.libraries().contains { $0.id == library.id })
        XCTAssertFalse(service.settings.enabledLibraryIds.contains(library.id))
    }

    func testRemoveThrowsForBuiltInLibrary() {
        guard let builtIn = BuiltInDictionaryLibraries.all.first else {
            return XCTFail("expected at least one built-in library")
        }

        XCTAssertThrowsError(try service.remove(id: builtIn.id)) { error in
            XCTAssertTrue(error is DictionaryLibraryServiceError)
        }
    }

    func testRemoveRejectsPathTraversalIds() {
        XCTAssertThrowsError(try service.remove(id: "../evil"))
    }

    func testEnabledLibraryEntriesComposesOnlySwitchedOnLibraries() throws {
        let library = try service.import(csv: "pattern,replacement\nfoo bar,FooBar\n", suggestedName: "enabled-test")

        service.settings.enabledLibraryIds = []
        XCTAssertTrue(service.enabledLibraryEntries().isEmpty)

        service.settings.enabledLibraryIds = [library.id]
        let entries = service.enabledLibraryEntries()
        XCTAssertEqual(entries.map(\.pattern), ["foo bar"])
    }
}

final class DictionaryLibrarySettingsTests: XCTestCase {
    private var defaults: StorageTestDefaults!
    private var settings: DictionaryLibrarySettings!

    override func setUp() {
        super.setUp()
        defaults = StorageTestDefaults()
        settings = DictionaryLibrarySettings(defaults: defaults.defaults)
    }

    override func tearDown() {
        defaults.remove()
        super.tearDown()
    }

    func testSetEnabledAddsAndRemovesIds() {
        XCTAssertTrue(settings.enabledLibraryIds.isEmpty)

        settings.setEnabled(true, id: "github")
        XCTAssertTrue(settings.enabledLibraryIds.contains("github"))

        settings.setEnabled(false, id: "github")
        XCTAssertFalse(settings.enabledLibraryIds.contains("github"))
    }

    func testChoicesLiveInTheInjectedDefaultsUnderTheShippingKey() {
        settings.setEnabled(true, id: "azure")

        XCTAssertEqual(defaults.defaults.stringArray(forKey: DictionaryLibrarySettings.enabledIdsKey), ["azure"])
    }
}
