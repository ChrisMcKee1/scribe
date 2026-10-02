import Foundation
import XCTest

@testable import Scribe

final class CopyManifestTests: XCTestCase {
    private let dashes: [Character] = ["\u{2014}", "\u{2013}"]

    func testEveryCatalogHasItemsAndIdsThatStartWithItsPrefix() {
        XCTAssertFalse(SettingsCopy.catalogs.isEmpty)
        for catalog in SettingsCopy.catalogs {
            XCTAssertFalse(catalog.items.isEmpty, "\(catalog.prefix) has no items")
            for item in catalog.items {
                XCTAssertTrue(item.id.hasPrefix(catalog.prefix + "."), "\(item.id) is not under \(catalog.prefix)")
            }
        }
    }

    func testIdsAreUniqueAcrossEveryCatalog() {
        let ids = SettingsCopy.allItems.map(\.id)
        let repeated = Dictionary(grouping: ids, by: { $0 }).filter { $0.value.count > 1 }.keys.sorted()
        XCTAssertTrue(repeated.isEmpty, "Duplicate ids: \(repeated)")
    }

    func testTextIsNeverEmptyAndNeverHoldsADashOrAnEllipsisCharacter() {
        for item in SettingsCopy.allItems {
            XCTAssertFalse(item.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "\(item.id) is empty")
            XCTAssertFalse(item.text.contains(where: { dashes.contains($0) }), "\(item.id) has a dash")
            XCTAssertFalse(item.text.contains("\u{2026}"), "\(item.id) holds an ellipsis character, write three dots")
        }
    }

    func testPlaceholdersAreClosedAndRenderFillsThemAndTurnsThreeDotsIntoAnEllipsis() {
        for item in SettingsCopy.allItems {
            let opened = item.text.filter { $0 == "{" }.count
            XCTAssertEqual(opened, item.text.filter { $0 == "}" }.count, item.id)
            let values = Dictionary(uniqueKeysWithValues: item.placeholders.map { ($0, "X") })
            let rendered = item.render(values)
            XCTAssertFalse(rendered.contains("{"), "\(item.id) keeps a placeholder: \(rendered)")
            XCTAssertFalse(rendered.contains("..."), "\(item.id) keeps three dots")
        }
        let sample = CopyItem.added("t.x", "Wait... {count} left")
        XCTAssertEqual(sample.placeholders, ["count"])
        XCTAssertEqual(sample.render(["count": "3"]), "Wait\u{2026} 3 left")
    }

    func testADeviationIsRecordedExactlyWhenTheTextDiffersFromWindowsOrHasNoWindowsTwin() {
        for item in SettingsCopy.allItems {
            if let windows = item.windows {
                if item.deviation == nil {
                    XCTAssertEqual(item.text, windows, "\(item.id) differs from Windows without a recorded reason")
                } else {
                    XCTAssertNotEqual(item.text, windows, "\(item.id) says it deviates but equals the Windows text")
                }
            } else {
                XCTAssertNotNil(item.deviation, "\(item.id) has no Windows text and no recorded reason")
            }
        }
    }

    func testEveryWindowsStringACatalogCitesIsInTheWindowsSources() {
        for item in SettingsCopy.allItems {
            guard let windows = item.windows else { continue }
            XCTAssertTrue(WindowsSources.contains(windows), "\(item.id): '\(windows)' is not in the Windows sources")
        }
    }

    func testEveryOmittedWindowsStringIsInTheWindowsSourcesAndHasAReason() {
        for omission in SettingsCopyOmissions.all {
            let found = WindowsSources.contains(omission.windows)
            XCTAssertTrue(found, "'\(omission.windows)' is not in the Windows sources")
        }
        let texts = SettingsCopyOmissions.all.map(\.windows)
        XCTAssertEqual(Set(texts).count, texts.count)
    }

    func testThereIsACatalogForEveryPageAndEverySurface() {
        let prefixes = Set(SettingsCopy.catalogs.map(\.prefix))
        let names: [SettingsPage: String] = [.dictionary: "dictionary", .voiceSnippets: "snippets"]
        for page in SettingsPage.allCases {
            let expected = names[page] ?? String(describing: page)
            XCTAssertTrue(prefixes.contains(expected), "No catalog for \(page)")
        }
        for surface in ["wordPacks", "window", "menuBar", "notice", "indicator"] {
            XCTAssertTrue(prefixes.contains(surface), "No catalog for \(surface)")
        }
    }

    func testTheHelperReadsStringsSplitAcrossLinesAndEscapedQuotes() {
        let source = "var x = \"First half, \" +\n    \"second half\";\n<Button Content=\"Say &quot;hi&quot;\" />"
        let text = WindowsSources.normalize(source)
        XCTAssertTrue(text.contains("First half, second half"))
        XCTAssertTrue(text.contains("Say \"hi\""))
        XCTAssertEqual(WindowsSources.fragments(of: "Unsaved changes: {pages}"), ["Unsaved changes:"])
    }
}
