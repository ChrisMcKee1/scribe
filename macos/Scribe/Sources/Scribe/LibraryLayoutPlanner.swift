import Foundation

struct LibraryLayoutInput: Sendable {
    let contentWidth: Double
    let height: Double
    var textScale: Double = 1
    var noticeVisible = false
    var termDetailsOpen = false
}

struct LibraryLayout: Equatable, Sendable {
    let sideBySide: Bool
    let short: Bool
    let listWidth: Double
    let useColumnWidth: Double
    let spokenColumnWidth: Double
    let writtenColumnWidth: Double
    let actionColumnWidth: Double
    let visibleTermRows: Int
    let horizontalOverflow: Bool
    let verticalOverflow: Bool
}

/// Reference editor geometry, not a SwiftUI layout. The native page may supply its own measured row metrics.
enum LibraryLayoutPlanner {
    static let minimumTextColumn: Double = 150
    static let minimumNormalRows = 6
    static let minimumRows = 4

    static func plan(_ input: LibraryLayoutInput, tabStrip: Double = 40) -> LibraryLayout {
        precondition(input.contentWidth > 0 && input.height > 0)
        let scale = input.textScale.isFinite ? min(2.25, max(1, input.textScale)) : 1
        let use = 54 * scale
        let action = 40 * scale
        let minimumText = minimumTextColumn * scale
        let list = min(220 * scale, 320)
        let sideText = max(0, input.contentWidth - list - 12 - 32 - use - action) / 2
        let sideBySide = sideText >= minimumText
        var text = sideBySide ? sideText : max(0, input.contentWidth - 32 - use - action) / 2
        let horizontalOverflow = !sideBySide && text < minimumText
        if horizontalOverflow { text = minimumText }
        func rows(short: Bool) -> Int {
            let subtitle = short ? 0 : 40 * scale
            let card = input.height - 32 - 20 - 16 - 32 * scale - 22 - 28 * scale
                - subtitle - 32 * scale - 10 - tabStrip * scale
            let notice = input.noticeVisible ? 48 * scale + 8 : 0
            let details = input.termDetailsOpen && !short ? 180 * scale : 0
            let inCard = 32 + 16 + 102 * scale + (short ? 0 : 20 * scale) + notice
                + 32 * scale + 8 + 32 * scale + details
            return Int(floor(max(0, card - inCard) / (20 * scale + 8)))
        }
        let short = rows(short: false) < minimumNormalRows
        let visible = rows(short: short)
        return LibraryLayout(
            sideBySide: sideBySide, short: short, listWidth: sideBySide ? list : input.contentWidth,
            useColumnWidth: use, spokenColumnWidth: text, writtenColumnWidth: text, actionColumnWidth: action,
            visibleTermRows: max(minimumRows, visible), horizontalOverflow: horizontalOverflow,
            verticalOverflow: visible < minimumRows)
    }
}
